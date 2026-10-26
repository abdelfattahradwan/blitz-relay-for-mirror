using BlitzRelay.Mirror.LiteNetLib;
using BlitzRelay.Protocol;
using JetBrains.Annotations;
using Mirror;
using System;
using UnityEngine;

namespace BlitzRelay.Mirror
{
	[AddComponentMenu("Network/Blitz Relay Transport")]
	public sealed class BlitzRelayTransport : Transport
	{
		[Header("Relay Connection")]
		[SerializeField]
		[Tooltip("Public IP address or hostname of the relay server.")]
		private string relayAddress = "127.0.0.1";

		[SerializeField]
		[Tooltip("Port of the relay server.")]
		private ushort relayPort = 7770;

		[SerializeField]
		[Tooltip("Connection key to use for relay connections.")]
		private string relayKey = string.Empty;

		[SerializeField]
		[Tooltip("Room code to join or claim on the relay server. For host-created rooms, the relay assigns this after room creation.")]
		private string roomCode = string.Empty;

		[SerializeField]
		[Tooltip("While true, forces sockets to send data directly to interface without routing.")]
		private bool doNotRoute;

		[Header("Server")]
		[SerializeField]
		[Tooltip("Maximum number of players which may be connected at once.")]
		[Range(1, 8192)]
		private int maximumClients = 4096;

		[SerializeField]
		[Tooltip("LiteNetLib inactivity timeout in milliseconds.")]
		[Min(1)]
		private int disconnectTimeoutMilliseconds = 30000;

		[Header("Messages")]
		[SerializeField]
		[Tooltip("Largest reliable message Mirror may send, in bytes. LiteNetLib splits messages larger than one packet across several packets.")]
		[Min(MaximumSinglePacketMessageSize)]
		private int maximumReliableMessageSize = 256 * 1024;

		private const string Scheme = "blitzrelay";

		private const int MaximumUdpMtu = 1350;

		private const int MaximumSinglePacketMessageSize = MaximumUdpMtu - NetConstants.ChanneledHeaderSize - MessageCodec.MaxRelayHeaderSize;

		private string _roomHostToken = string.Empty;

		private string _pendingPromotedClaimToken = string.Empty;

		private int _pendingPromotedMaximumClients;

		private string _transportFailureReason;

		public readonly HostSocket ServerSocket = new();

		public readonly ClientSocket ClientSocket = new();

		public bool IsRelayHostAvailable { get; private set; }

		public bool HasPendingHostPromotion
		{
			get => !string.IsNullOrWhiteSpace(_pendingPromotedClaimToken);
		}

		internal bool DoNotRoute
		{
			get => doNotRoute;
		}

		public event Action<bool> OnRelayHostAvailabilityChanged;

		public event Action<HostPromotion> OnHostPromotionReceived;

		public event Action<string> OnTransportFailure;

		public override bool Available()
		{
			return Application.platform != RuntimePlatform.WebGLPlayer;
		}

		public override bool ClientConnected()
		{
			return ClientSocket.SocketState == RelaySocketState.Started;
		}

		public override void ClientConnect(string address)
		{
			ClearPendingHostPromotion();

			InitialiseSockets();

			UpdateTimeout();

			ClientSocket.StartConnection(relayAddress, relayPort, relayKey, roomCode);
		}

		public override void ClientSend(ArraySegment<byte> segment, int channelId = Channels.Reliable)
		{
			if (ClientSocket.SendToServer(SocketBase.GetGameChannel(channelId), segment)) OnClientDataSent?.Invoke(segment, channelId);
		}

		public override void ClientDisconnect()
		{
			ClientSocket.StopConnection(true);
		}

		public override Uri ServerUri()
		{
			return TryBuildValidUri(Scheme, relayAddress, relayPort);
		}

		public override bool ServerActive()
		{
			return ServerSocket.SocketState is RelaySocketState.Starting or RelaySocketState.Started;
		}

		public override void ServerStart()
		{
			InitialiseSockets();

			UpdateTimeout();

			if (!HasPendingHostPromotion) roomCode = string.Empty;

			int requestedMaximumClients = _pendingPromotedMaximumClients > 0 ? _pendingPromotedMaximumClients : maximumClients;

			ServerSocket.StartConnection(relayAddress, relayPort, relayKey, requestedMaximumClients, _pendingPromotedClaimToken, roomCode);

			ClearPendingHostPromotion();
		}

		public override void ServerSend(int connectionId, ArraySegment<byte> segment, int channelId = Channels.Reliable)
		{
			if (ServerSocket.SendToClient(SocketBase.GetGameChannel(channelId), segment, connectionId)) OnServerDataSent?.Invoke(connectionId, segment, channelId);
		}

		public override void ServerDisconnect(int connectionId)
		{
			ServerSocket.DisconnectRemoteClient(connectionId);
		}

		public override string ServerGetClientAddress(int connectionId)
		{
			return ServerSocket.GetConnectionAddress(connectionId);
		}

		public override void ServerStop()
		{
			ServerSocket.StopConnection(false);

			_roomHostToken = string.Empty;

			_transportFailureReason = null;
		}

		public override int GetMaxPacketSize(int channelId = Channels.Reliable)
		{
			return channelId == Channels.Unreliable ? MaximumSinglePacketMessageSize : maximumReliableMessageSize;
		}

		public override int GetBatchThreshold(int channelId = Channels.Reliable)
		{
			return MaximumSinglePacketMessageSize;
		}

		public override void ClientEarlyUpdate()
		{
			if (enabled) ClientSocket.PollSocket();
		}

		public override void ServerEarlyUpdate()
		{
			if (enabled) ServerSocket.PollSocket();

			if (_transportFailureReason == null) return;

			string reason = _transportFailureReason;

			_transportFailureReason = null;

			OnTransportFailure?.Invoke(reason);
		}

		public override void ClientLateUpdate()
		{
			ClientSocket.Flush();
		}

		public override void ServerLateUpdate()
		{
			ServerSocket.Flush();
		}

		public override void Shutdown()
		{
			ClientSocket.StopConnection(false);

			ServerSocket.StopConnection(false);

			_roomHostToken = string.Empty;

			_transportFailureReason = null;
		}

		public string GetRelayAddress()
		{
			return relayAddress;
		}

		public void SetRelayAddress(string newRelayAddress)
		{
			relayAddress = newRelayAddress;
		}

		public ushort GetRelayPort()
		{
			return relayPort;
		}

		public void SetRelayPort(ushort newRelayPort)
		{
			relayPort = newRelayPort;
		}

		public string GetRelayKey()
		{
			return relayKey;
		}

		public void SetRelayKey(string newRelayKey)
		{
			relayKey = newRelayKey ?? string.Empty;
		}

		public string GetRoomCode()
		{
			return roomCode;
		}

		public void SetRoomCode(string newRoomCode)
		{
			roomCode = newRoomCode ?? string.Empty;
		}

		public string GetRoomHostToken()
		{
			return _roomHostToken;
		}

		public void SetRoomHostToken([NotNull] string newRoomHostToken)
		{
			_roomHostToken = newRoomHostToken;
		}

		public int GetMaximumClients()
		{
			return maximumClients;
		}

		public void SetMaximumClients(int newMaximumClients)
		{
			maximumClients = newMaximumClients;
		}

		public void ClearPendingHostPromotion()
		{
			_pendingPromotedClaimToken = string.Empty;

			_pendingPromotedMaximumClients = 0;
		}

		internal void HandleClientHostPromoted(string promotedRoomCode, int promotedMaximumClients, string claimToken)
		{
			roomCode = promotedRoomCode;

			_pendingPromotedMaximumClients = promotedMaximumClients;

			_pendingPromotedClaimToken = claimToken;

			HandleRelayHostAvailability(false);

			try
			{
				OnHostPromotionReceived?.Invoke(new HostPromotion(promotedRoomCode, promotedMaximumClients));
			}
			catch (Exception exception)
			{
				Debug.LogException(exception);
			}
		}

		internal void HandleRelayHostAvailability(bool isAvailable)
		{
			if (IsRelayHostAvailable == isAvailable) return;

			IsRelayHostAvailable = isAvailable;

			try
			{
				OnRelayHostAvailabilityChanged?.Invoke(isAvailable);
			}
			catch (Exception exception)
			{
				Debug.LogException(exception);
			}
		}

		internal void EnqueueTransportFailure(string reason)
		{
			_transportFailureReason = reason;
		}

		private void OnDestroy()
		{
			Shutdown();
		}

		private void InitialiseSockets()
		{
			ServerSocket.Initialise(this, MaximumUdpMtu);

			ClientSocket.Initialise(this, MaximumUdpMtu);
		}

		private void UpdateTimeout()
		{
			ServerSocket.UpdateTimeout(disconnectTimeoutMilliseconds);

			ClientSocket.UpdateTimeout(disconnectTimeoutMilliseconds);
		}
	}
}
