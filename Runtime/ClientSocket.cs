using BlitzRelay.Mirror.LiteNetLib;
using BlitzRelay.Protocol;
using Mirror;
using System;
using System.Buffers;

namespace BlitzRelay.Mirror
{
	public sealed class ClientSocket : SocketBase
	{
		private bool _pendingPromotionDisconnect;

		internal void StartConnection(string relayAddress, ushort relayPort, string relayKey, string roomCode)
		{
			if (SocketState != RelaySocketState.Stopped) StopConnection(false);

			SocketState = RelaySocketState.Starting;

			RoomCode = roomCode;

			_pendingPromotionDisconnect = false;

			EventBasedNetListener listener = new();

			listener.PeerConnectedEvent += PeerConnectedEventHandler;

			listener.PeerDisconnectedEvent += PeerDisconnectedEventHandler;

			listener.NetworkReceiveEvent += NetworkReceiveEventHandler;

			if (StartRelayConnection(listener, relayAddress, relayPort, relayKey, out TransportError error, out string reason)) return;

			SocketState = RelaySocketState.Stopped;

			Transport.OnClientError?.Invoke(error, reason);

			Transport.OnClientDisconnected?.Invoke();
		}

		internal void StopConnection(bool notifyMirror)
		{
			if (SocketState is RelaySocketState.Stopping or RelaySocketState.Stopped) return;

			SocketState = RelaySocketState.Stopping;

			_pendingPromotionDisconnect = false;

			StopSocket();

			if (notifyMirror) Transport.OnClientDisconnected?.Invoke();

			Transport.HandleRelayHostAvailability(false);
		}

		internal bool SendToServer(byte gameChannel, ArraySegment<byte> payload)
		{
			if (SocketState != RelaySocketState.Started || RelayPeer.ConnectionState != ConnectionState.Connected) return false;

			DeliveryMethod deliveryMethod = GetDeliveryMethod(gameChannel);

			int frameSize = MessageCodec.ClientDataHeaderSize + payload.Count;

			if (frameSize > RelayPeer.GetMaxSinglePacketSize(deliveryMethod))
			{
				if (MessageCodec.IsUnreliableGameChannel(gameChannel)) return false;

				byte[] frameBuffer = ArrayPool<byte>.Shared.Rent(frameSize);

				try
				{
					int written = MessageCodec.WriteClientData(frameBuffer, gameChannel, payload);

					RelayPeer.Send(frameBuffer, 0, written, MessageCodec.RelayWireChannel, deliveryMethod);
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(frameBuffer);
				}
			}
			else
			{
				PooledPacket pooledPacket = RelayPeer.CreatePacketFromPool(deliveryMethod, MessageCodec.RelayWireChannel);

				int written = MessageCodec.WriteClientData(pooledPacket.Data.AsSpan(pooledPacket.UserDataOffset, frameSize), gameChannel, payload);

				RelayPeer.SendPooledPacket(pooledPacket, written);
			}

			IsFlushPending = true;

			return true;
		}

		private static TransportError GetTransportError(ErrorCode errorCode)
		{
			return errorCode switch
			{
				ErrorCode.RoomNotFound or ErrorCode.RoomFull => TransportError.Refused,

				ErrorCode.RoomClosed => TransportError.ConnectionClosed,

				_ => TransportError.Unexpected,
			};
		}

		private static TransportError GetTransportError(DisconnectReason disconnectReason)
		{
			return disconnectReason switch
			{
				DisconnectReason.ConnectionFailed or DisconnectReason.Timeout => TransportError.Timeout,

				DisconnectReason.ConnectionRejected => TransportError.Refused,

				_ => TransportError.ConnectionClosed,
			};
		}

		private void PeerConnectedEventHandler(NetPeer peer)
		{
			if (!ReferenceEquals(peer, RelayPeer)) return;

			byte[] joinMessage = MessageCodec.CreateClientJoin(RoomCode);

			peer.Send(joinMessage, MessageCodec.RelayWireChannel, DeliveryMethod.ReliableOrdered);
		}

		private void PeerDisconnectedEventHandler(NetPeer peer, DisconnectInfo disconnectInfo)
		{
			if (!ReferenceEquals(peer, RelayPeer)) return;

			if (MessageCodec.TryReadError(disconnectInfo.AdditionalData.GetRemainingBytesSpan(), out ErrorCode errorCode))
			{
				Transport.ClearPendingHostPromotion();

				Transport.OnClientError?.Invoke(GetTransportError(errorCode), $"Relay returned error code {errorCode}.");
			}
			else if (!_pendingPromotionDisconnect && disconnectInfo.Reason != DisconnectReason.RemoteConnectionClose)
			{
				Transport.OnClientError?.Invoke(GetTransportError(disconnectInfo.Reason), $"Relay client connection closed. Reason: {disconnectInfo.Reason}.");
			}

			if (!ReferenceEquals(peer, RelayPeer)) return;

			StopConnection(true);
		}

		private void NetworkReceiveEventHandler(NetPeer fromPeer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
		{
			try
			{
				if (!ReferenceEquals(fromPeer, RelayPeer)) return;

				int dataLength = reader.UserDataSize;

				if (dataLength < 1) return;

				ReadOnlySpan<byte> relayPayload = new(reader.RawData, reader.UserDataOffset, dataLength);

				switch (MessageCodec.ReadMessageType(relayPayload))
				{
					case MessageType.JoinSuccess:
					{
						SocketState = RelaySocketState.Started;

						Transport.HandleRelayHostAvailability(true);

						if (!ReferenceEquals(fromPeer, RelayPeer)) break;

						Transport.OnClientConnected?.Invoke();

						break;
					}

					case MessageType.HostPromoted:
					{
						MessageCodec.ReadHostPromoted(relayPayload, out string roomCode, out int maximumClients, out string claimToken);

						_pendingPromotionDisconnect = true;

						byte[] acknowledgement = MessageCodec.CreateHostPromotionAck(roomCode, claimToken);

						fromPeer.Send(acknowledgement, MessageCodec.RelayWireChannel, DeliveryMethod.ReliableOrdered);

						fromPeer.NetManager.TriggerUpdate();

						Transport.HandleClientHostPromoted(roomCode, maximumClients, claimToken);

						break;
					}

					case MessageType.HostUnavailable:
					{
						Transport.HandleRelayHostAvailability(false);

						break;
					}

					case MessageType.HostAvailable:
					{
						Transport.HandleRelayHostAvailability(true);

						break;
					}

					case MessageType.Data:
					{
						if (SocketState != RelaySocketState.Started) break;

						MessageCodec.ReadClientData(relayPayload, out byte gameChannel, out ReadOnlySpan<byte> gamePayload);

						ArraySegment<byte> segment = new(reader.RawData, reader.UserDataOffset + MessageCodec.ClientDataHeaderSize, gamePayload.Length);

						Transport.OnClientDataReceived?.Invoke(segment, MessageCodec.IsUnreliableGameChannel(gameChannel) ? Channels.Unreliable : Channels.Reliable);

						break;
					}

					case MessageType.Disconnected:
					{
						StopConnection(true);

						break;
					}

					case MessageType.Error:
					{
						ErrorCode errorCode = MessageCodec.ReadError(relayPayload);

						Transport.ClearPendingHostPromotion();

						Transport.OnClientError?.Invoke(GetTransportError(errorCode), $"Relay returned error code {errorCode}.");

						if (!ReferenceEquals(fromPeer, RelayPeer)) break;

						StopConnection(true);

						break;
					}
				}
			}
			finally
			{
				reader.Recycle();
			}
		}
	}
}
