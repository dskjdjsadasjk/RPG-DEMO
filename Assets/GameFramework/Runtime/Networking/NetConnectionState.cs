namespace RPGDemo.GameFramework.Networking
{
    public enum NetworkProcessMode : byte
    {
        None,
        DedicatedServer,
        Client
    }

    public enum NetConnectionState : byte
    {
        Connecting,
        AwaitHello,
        AwaitChallenge,
        AwaitLogin,
        AwaitWelcome,
        AwaitReady,
        Ready,
        Disconnecting,
        Disconnected
    }
}
