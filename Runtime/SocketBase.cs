using BlitzRelay.Mirror.LiteNetLib;
using BlitzRelay.Protocol;
using Mirror;
using System;

namespace BlitzRelay.Mirror
{
	public abstract class SocketBase
	{
		protected string RoomCode = string.Empty;

		protected int DisconnectTimeoutMilliseconds = 30000;

		protected int Mtu;

		protected NetPeer RelayPeer;

		protected BlitzRelayTransport Transport;

		protected bool IsFlushPending;

		private NetManager _polledNetManager;

		public NetManager SocketNetManager { get; protected set; }

		public RelaySocketState SocketState { get; protected set; } = RelaySocketState.Stopped;

		internal void Initialise(BlitzRelayTransport transport, int mtu)
		{
			Transport = transport ?? throw new ArgumentNullException(nameof(transport));

			Mtu = mtu;
		}

		internal void UpdateTimeout(int timeoutMilliseconds)
		{
			DisconnectTimeoutMilliseconds = timeoutMilliseconds <= 0 ? int.MaxValue : timeoutMilliseconds;

			if (SocketNetManager != null) SocketNetManager.DisconnectTimeout = DisconnectTimeoutMilliseconds;
		}

		internal void PollSocket()
		{
			NetManager netManager = SocketNetManager;

			if (netManager == null) return;

			_polledNetManager = netManager;

			try
			{
				netManager.PollEvents();
			}
			finally
			{
				_polledNetManager = null;

				if (SocketNetManager != netManager) netManager.Stop();
			}
		}

		internal void Flush()
		{
			if (!IsFlushPending) return;

			IsFlushPending = false;

			SocketNetManager.TriggerUpdate();
		}

		protected bool StartRelayConnection(EventBasedNetListener listener, string address, ushort port, string key, out TransportError error, out string reason)
		{
			NetManager netManager = new(listener)
			{
				DisconnectTimeout = DisconnectTimeoutMilliseconds,

				DontRoute = Transport.DoNotRoute,

				MtuOverride = Mtu,
			};

			if (!netManager.Start())
			{
				error = TransportError.Unexpected;

				reason = "Failed to start the relay socket.";

				return false;
			}

			NetPeer relayPeer = netManager.Connect(address, port, key);

			if (relayPeer == null)
			{
				netManager.Stop(false);

				error = TransportError.DnsResolve;

				reason = $"Failed to resolve the relay address '{address}'.";

				return false;
			}

			SocketNetManager = netManager;

			RelayPeer = relayPeer;

			error = default(TransportError);

			reason = string.Empty;

			return true;
		}

		internal static byte GetGameChannel(int channelId)
		{
			return channelId == Channels.Unreliable ? (byte)GameChannel.Unreliable : (byte)GameChannel.Reliable;
		}

		protected static DeliveryMethod GetDeliveryMethod(byte gameChannel)
		{
			return gameChannel == (byte)GameChannel.Unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered;
		}

		protected void StopSocket()
		{
			if (SocketNetManager != _polledNetManager) SocketNetManager?.Stop();

			SocketNetManager = null;

			RelayPeer = null;

			IsFlushPending = false;

			SocketState = RelaySocketState.Stopped;
		}
	}
}
