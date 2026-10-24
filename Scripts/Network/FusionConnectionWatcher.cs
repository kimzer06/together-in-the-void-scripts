using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// ตัวจับ "ห้องจบ" ตลอดทั้งเกม (แทน OnTransportFailure / OnClientStopped ใน LobbySelectionState ของ NGO)
/// ผูกกับ runner ตอนเข้าห้องผ่านเมนูจริง (<see cref="FusionSession.StartSharedAsync"/>) — QuickTest ไม่ใช้
///
/// - คนสร้างห้อง (Player A) ออก → อีกคนกลับ StartScene
///   (Shared Mode จะย้าย master ให้เราแทนแต่เกม co-op เล่นต่อคนเดียวไม่ได้)
/// - คนเข้าห้อง (Player B) ออก → หน้าเลือกตัวละคร/เครดิต: คนสร้างห้องอยู่ต่อ (ช่องว่างให้คนใหม่เข้า) · ในด่าน: กลับ StartScene
/// - เราหลุดเอง (เน็ต/Photon ตัด) → กลับ StartScene
/// ทุกกรณีฝากเหตุผลไว้ที่ <see cref="FusionSession.PendingMenuMessage"/> → StartScene โชว์กล่องกลางจอ (<see cref="ConnectionMessagePopup"/>)
/// </summary>
public class FusionConnectionWatcher : INetworkRunnerCallbacks
{
    public const string PartnerLeftMessage = "Your partner left the game.";
    public const string LostConnectionMessage = "Lost connection to the server.";
    public const string NoInternetMessage = "No internet connection.";   // ใช้ใน toast หน้า Join ด้วย — กรอบแคบ อย่ายาว

    // ซีนที่คนสร้างห้องอยู่ต่อได้เมื่ออีกคนออก — นอกนั้น (Loading / ด่าน) เล่นคนเดียวไม่ได้
    private static readonly string[] HostStaysScenes = { "CharacterSelect", "EndCredit" };

    private static bool s_quitting;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Init()
    {
        // Domain Reload ปิดอยู่ → รีเซ็ตเองทุกรอบ Play · Editor ยิง quitting ตอนออก Play mode ด้วย
        s_quitting = false;
        Application.quitting -= OnQuitting;
        Application.quitting += OnQuitting;
    }

    private static void OnQuitting() => s_quitting = true;

    public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
    {
        if (player == runner.LocalPlayer || FusionSession.IsLeaving) return;
        if (FusionSession.IsHost && Array.IndexOf(HostStaysScenes, SceneManager.GetActiveScene().name) >= 0) return;

        Debug.Log($"[FusionConnectionWatcher] Partner {player} left (host={FusionSession.IsHost}) → {FusionSession.MenuSceneName}");
        ReturnToMenu(PartnerLeftMessage);
    }

    public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
    {
        if (s_quitting || FusionSession.IsLeaving) return;

        Debug.Log($"[FusionConnectionWatcher] Disconnected ({shutdownReason}) → {FusionSession.MenuSceneName}");
        ReturnToMenu(Application.internetReachability == NetworkReachability.NotReachable
            ? NoInternetMessage
            : LostConnectionMessage);
    }

    private static void ReturnToMenu(string message)
    {
        FusionSession.ShutdownAll();
        FusionSession.PendingMenuMessage = message;
        SceneManager.LoadScene(FusionSession.MenuSceneName);
    }

    // ───── ไม่ใช้ ─────
    public void OnPlayerJoined(NetworkRunner runner, PlayerRef player) { }
    public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    public void OnInput(NetworkRunner runner, NetworkInput input) { }
    public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
    public void OnConnectedToServer(NetworkRunner runner) { }
    public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
    public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
    public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
#pragma warning disable CS0618 // ยังอยู่ใน interface แม้ Fusion ประกาศ obsolete
    public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
#pragma warning restore CS0618
    public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
    public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
    public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
    public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ReadOnlySpan<byte> data) { }
    public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
    public void OnSceneLoadDone(NetworkRunner runner) { }
    public void OnSceneLoadStart(NetworkRunner runner) { }
}
