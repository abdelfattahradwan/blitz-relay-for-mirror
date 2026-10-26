using BlitzRelay.Mirror.LiteNetLib;
using BlitzRelay.Protocol;
using Mirror;
using System;
using System.Buffers;
using System.Collections.Generic;
using UnityEngine;

namespace BlitzRelay.Mirror
{
	public sealed class HostSocket : SocketBase
	{
		private readonly HashSet<int> _virtualClients = new();

		private readonly List<int> _disconnectedClients = new();

		private int _maximumClients;

		private string _claimToken = string.Empty;

		internal void StartConnection(string relayAddress, ushort relayPort, string relayKey, int maximumClients, string claimToken, string roomCode)
		{
			if (SocketState != RelaySocketState.Stopped) StopConnection(false);

			SocketState = RelaySocketState.Starting;

			RoomCode = roomCode;

			_maximumClients = maximumClients;

			_claimToken = claimToken;

			EventBasedNetListener listener = new();

			listener.PeerConnectedEvent += PeerConnectedEventHandler;

			listener.PeerDisconnectedEvent += PeerDisconnectedEventHandler;

			listener.NetworkReceiveEvent += NetworkReceiveEventHandler;

			if (StartRelayConnection(listener, relayAddress, relayPort, relayKey, out _, out string reason)) return;

			SocketState = RelaySocketState.Stopped;

			Debug.LogError(reason);

			Transport.EnqueueTransportFailure(reason);
		}

		internal void StopConnection(bool notifyMirror)
		{
			if (SocketState is RelaySocketState.Stopping or RelaySocketState.Stopped) return;

			SocketState = RelaySocketState.Stopping;

			if (notifyMirror) _disconnectedClients.AddRange(_virtualClients);

			_virtualClients.Clear();

			_claimToken = string.Empty;

			StopSocket();

			Transport.HandleRelayHostAvailability(false);

			foreach (int connectionId in _disconnectedClients)
			{
				Transport.OnServerDisconnected?.Invoke(connectionId);
			}

			_disconnectedClients.Clear();
		}

		internal void DisconnectRemoteClient(int connectionId)
		{
			if (!_virtualClients.Remove(connectionId)) return;

			byte[] kickMessage = MessageCodec.CreateKick(connectionId);

			RelayPeer.Send(kickMessage, MessageCodec.RelayWireChannel, DeliveryMethod.ReliableOrdered);

			Transport.OnServerDisconnected?.Invoke(connectionId);
		}

		internal bool SendToClient(byte gameChannel, ArraySegment<byte> payload, int connectionId)
		{
			if (SocketState != RelaySocketState.Started || RelayPeer.ConnectionState != ConnectionState.Connected || !_virtualClients.Contains(connectionId)) return false;

			DeliveryMethod deliveryMethod = GetDeliveryMethod(gameChannel);

			int frameSize = MessageCodec.HostDataHeaderSize + payload.Count;

			if (frameSize > RelayPeer.GetMaxSinglePacketSize(deliveryMethod))
			{
				if (MessageCodec.IsUnreliableGameChannel(gameChannel)) return false;

				byte[] frameBuffer = ArrayPool<byte>.Shared.Rent(frameSize);

				try
				{
					int written = MessageCodec.WriteHostData(frameBuffer, connectionId, gameChannel, payload);

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

				int written = MessageCodec.WriteHostData(pooledPacket.Data.AsSpan(pooledPacket.UserDataOffset, frameSize), connectionId, gameChannel, payload);

				RelayPeer.SendPooledPacket(pooledPacket, written);
			}

			IsFlushPending = true;

			return true;
		}

		internal string GetConnectionAddress(int connectionId)
		{
			return _virtualClients.Contains(connectionId) ? $"relay:{connectionId}" : string.Empty;
		}

		private void PeerConnectedEventHandler(NetPeer peer)
		{
			if (!ReferenceEquals(peer, RelayPeer)) return;

			byte[] registerMessage = string.IsNullOrWhiteSpace(_claimToken) ? MessageCodec.CreateHostRegister(_maximumClients) : MessageCodec.CreateHostClaim(RoomCode, _claimToken);

			peer.Send(registerMessage, MessageCodec.RelayWireChannel, DeliveryMethod.ReliableOrdered);
		}

		private void PeerDisconnectedEventHandler(NetPeer peer, DisconnectInfo disconnectInfo)
		{
			if (!ReferenceEquals(peer, RelayPeer)) return;

			try
			{
				string reason;

				if (MessageCodec.TryReadError(disconnectInfo.AdditionalData.GetRemainingBytesSpan(), out ErrorCode errorCode))
				{
					reason = $"Relay returned error code {errorCode}.";

					Debug.LogWarning(reason);
				}
				else
				{
					reason = $"Relay host connection lost. Reason: {disconnectInfo.Reason}.";

					Debug.Log(reason);
				}

				Transport.EnqueueTransportFailure(reason);

				StopConnection(true);
			}
			catch (Exception exception)
			{
				Debug.LogException(exception);
			}
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
					case MessageType.RoomCreated:
					{
						MessageCodec.ReadRoomCreated(relayPayload, out string roomCode, out string roomHostToken);

						SocketState = RelaySocketState.Started;

						Transport.SetRoomCode(roomCode);

						Transport.SetRoomHostToken(roomHostToken);

						Transport.HandleRelayHostAvailability(true);

						break;
					}

					case MessageType.Connected:
					{
						int virtualClientId = MessageCodec.ReadConnected(relayPayload);

						if (_virtualClients.Count >= _maximumClients)
						{
							byte[] kickMessage = MessageCodec.CreateKick(virtualClientId);

							RelayPeer.Send(kickMessage, MessageCodec.RelayWireChannel, DeliveryMethod.ReliableOrdered);

							break;
						}

						if (_virtualClients.Add(virtualClientId)) Transport.OnServerConnectedWithAddress?.Invoke(virtualClientId, GetConnectionAddress(virtualClientId));

						break;
					}

					case MessageType.Disconnected:
					{
						int virtualClientId = MessageCodec.ReadDisconnected(relayPayload);

						if (_virtualClients.Remove(virtualClientId)) Transport.OnServerDisconnected?.Invoke(virtualClientId);

						break;
					}

					case MessageType.Data:
					{
						MessageCodec.ReadHostData(relayPayload, out int sourceVirtualClientId, out byte gameChannel, out ReadOnlySpan<byte> gamePayload);

						if (!_virtualClients.Contains(sourceVirtualClientId)) break;

						ArraySegment<byte> segment = new(reader.RawData, reader.UserDataOffset + MessageCodec.HostDataHeaderSize, gamePayload.Length);

						Transport.OnServerDataReceived?.Invoke(sourceVirtualClientId, segment, MessageCodec.IsUnreliableGameChannel(gameChannel) ? Channels.Unreliable : Channels.Reliable);

						break;
					}

					case MessageType.Error:
					{
						ErrorCode errorCode = MessageCodec.ReadError(relayPayload);

						string reason = $"Relay returned error code {errorCode}.";

						Debug.LogError(reason);

						Transport.EnqueueTransportFailure(reason);

						StopConnection(true);

						break;
					}
				}
			}
			catch (Exception exception)
			{
				Debug.LogException(exception);
			}
			finally
			{
				reader.Recycle();
			}
		}
	}
}
