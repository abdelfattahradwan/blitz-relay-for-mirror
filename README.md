# Blitz Relay for Mirror

A relay transport for [Mirror](https://github.com/MirrorNetworking/Mirror) that routes all client traffic through a
[Blitz Relay](https://github.com/abdelfattahradwan/blitz-relay) server instance, removing the need for clients to have
publicly reachable IP addresses or open ports. This makes it suitable for deployments where NAT traversal or firewall
restrictions make direct peer-to-peer connections unreliable or impractical.

The transport uses a modified build of [LiteNetLib](https://github.com/RevenantX/LiteNetLib) internally for its UDP
connections to the relay.

## Requirements

- Unity 6000.0 or later
- The latest [Mirror](https://github.com/MirrorNetworking/Mirror/releases) release from GitHub
- A [Blitz Relay](https://github.com/abdelfattahradwan/blitz-relay) server running v1.3.1 or later

## Installation

Open the Unity Package Manager (Window > Package Manager) and add the package via git URL:

```
https://github.com/abdelfattahradwan/blitz-relay-for-mirror.git
```

Or add the following entry to your project's `Packages/manifest.json`:

```json
"com.winterboltgames.blitzrelayformirror": "https://github.com/abdelfattahradwan/blitz-relay-for-mirror.git"
```

## Features

- **Relay-based architecture**: All game traffic flows through a relay server, so clients never need to expose their
  IP addresses or open firewall ports.
- **Room-based**: Hosts create rooms and receive a room code from the relay; clients join by supplying that room code.
- **Basic host migration**: When the host of a persistent room disconnects, the relay promotes an existing client to
  become the new host. See [Host Promotion](#host-promotion).
- **Host removal**: When the relay removes the host through its HTTP API (Blitz Relay v1.4.0 or later), the transport
  raises `OnTransportFailure` with the `HostRemoved` error. See [Relay Errors](#relay-errors).

## Setup

1. Add a GameObject with a `NetworkManager` component to your scene.
2. Add a `BlitzRelayTransport` component to the same GameObject.
3. Assign the `BlitzRelayTransport` component to the `NetworkManager`'s **Transport** field.
4. For hosts, set the relay address, port, key, and maximum clients, then call `NetworkManager.StartHost()` or
   `NetworkManager.StartServer()`. Once the relay has created the room, `OnRelayHostAvailabilityChanged(true)` fires and
   `GetRoomCode()` returns the code to share with clients.
5. For clients, set the relay address, port, key, and room code, then call `NetworkManager.StartClient()`. The transport
   ignores `NetworkManager.networkAddress`, but Mirror doesn't start a client while it's empty.

## Configuration

The following fields are exposed in the Inspector:

| Field                               | Type     | Description                                                                                |
|-------------------------------------|----------|--------------------------------------------------------------------------------------------|
| **Relay Address**                   | `string` | IP address or hostname of the relay server. Defaults to `127.0.0.1`.                       |
| **Relay Port**                      | `ushort` | Port number the relay server is listening on. Defaults to `7770`.                          |
| **Relay Key**                       | `string` | Connection key used to authenticate with the relay.                                        |
| **Room Code**                       | `string` | Room code to join on the relay. Hosts receive this from the relay after room creation.     |
| **Do Not Route**                    | `bool`   | When enabled, packets are sent directly to the network interface without OS-level routing. |
| **Maximum Clients**                 | `int`    | Maximum number of simultaneous client connections. Defaults to `4096`.                     |
| **Disconnect Timeout Milliseconds** | `int`    | LiteNetLib inactivity timeout. Defaults to `30000`.                                        |
| **Maximum Reliable Message Size**   | `int`    | Largest reliable message Mirror may send, in bytes. Defaults to `262144`.                  |

## Runtime API

Several methods and events are available for script-driven configuration:

- `SetRelayAddress(string)` / `GetRelayAddress()` - Change or read the relay server address.
- `SetRelayPort(ushort)` / `GetRelayPort()` - Change or read the relay server port.
- `SetRelayKey(string)` / `GetRelayKey()` - Change or read the relay connection key.
- `SetRoomCode(string)` / `GetRoomCode()` - Set or retrieve the current room code. A host starting a new room clears it
  until the relay assigns the new code.
- `SetRoomHostToken(string)` / `GetRoomHostToken()` - Manage the host token assigned by the relay.
- `SetMaximumClients(int)` / `GetMaximumClients()` - Adjust or read the client limit.
- `IsRelayHostAvailable` - Indicates whether the relay host is currently available.
- `OnRelayHostAvailabilityChanged` - Event that fires when host availability changes.
- `HasPendingHostPromotion` - Indicates whether this client has promotion data waiting to be claimed.
- `ClearPendingHostPromotion()` - Clears pending host promotion data.
- `OnHostPromotionReceived` - Event that fires when the relay promotes this client to host.
- `OnTransportFailure` - Event that fires when the host's connection to the relay fails. See
  [Relay Errors](#relay-errors).

## Host Promotion

When a client is promoted to host, the transport acknowledges the promotion, stores the claim data, and raises
`OnHostPromotionReceived`. The relay then closes the client's connection, and Mirror stops the client. Once
`NetworkClient.active` is `false`, start host mode with `NetworkManager.StartHost()`. Don't stop the client in the
`OnHostPromotionReceived` handler, because stopping it right away can drop the acknowledgement. The next server start
claims the promoted room automatically and uses up the claim data, whether the claim succeeds or not. Starting a client
session instead discards it.

Clients that weren't promoted stay in the room, but Mirror doesn't reconnect them to the new host. Keep each client
session running until `OnRelayHostAvailabilityChanged` reports that a host is available, then stop it with
`NetworkManager.StopClient()` and start a new client session to join the new host.

The new host starts a new Mirror server, so the game state of the old session isn't carried over.

## Relay Errors

When the relay rejects or closes a client's connection, for example with `RoomNotFound`, `RoomFull`, or `RoomClosed`,
the transport reports a client error, such as `Relay returned error code RoomFull.`, and disconnects the client. Mirror
then calls `NetworkManager.OnClientError` and `NetworkManager.OnClientDisconnect`.

When the relay rejects or closes the host's connection, for example with `HostRemoved` or `RoomClosed`, or the host
can't reach the relay, the transport disconnects every remote client and raises `OnTransportFailure` with the error.
Mirror doesn't stop the host session on its own, so stop it in an `OnTransportFailure` handler, for example with
`NetworkManager.StopHost()`.

## Licence

MIT License. See [LICENSE](LICENSE) for details.

Copyright 2026 Abdelfattah Radwan
