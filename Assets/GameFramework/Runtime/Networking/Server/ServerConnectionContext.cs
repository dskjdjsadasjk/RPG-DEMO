using System;
using RPGDemo.GameFramework.Networking.Identity;

namespace RPGDemo.GameFramework.Networking.Server
{
    public sealed class ServerConnectionContext
    {
        internal ServerConnectionContext(
            GameNetConnection connection,
            ServerPlayerController playerController,
            PlayerState playerState)
        {
            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
            PlayerController = playerController != null
                ? playerController
                : throw new ArgumentNullException(nameof(playerController));
            PlayerState = playerState != null
                ? playerState
                : throw new ArgumentNullException(nameof(playerState));
        }

        public GameNetConnection Connection { get; }
        public ServerPlayerController PlayerController { get; }
        public PlayerState PlayerState { get; }
        public NetworkIdentity PawnIdentity { get; internal set; }
    }
}
