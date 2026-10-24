using System.IO;
using System.Threading.Tasks;
using Fusion;
using UnityEngine.SceneManagement;

/// <summary>
/// ข้อมูลของเครื่องนี้ที่ต้องพกข้ามซีน (แทน LobbySelectionState ฝั่ง NGO ระหว่างย้าย)
/// เขียนโดย launcher / CharacterSelect, อ่านโดย FusionPlayerSpawner
/// + ทางลัดไปหา NetworkRunner ที่รันอยู่ สำหรับสคริปต์ที่ไม่ใช่ NetworkBehaviour
/// </summary>
public static class FusionSession
{
    public const byte NoSlot = 255;

    /// <summary>0 = Player A, 1 = Player B, NoSlot = ยังไม่กำหนด (spawner จะเดาจาก master client)</summary>
    public static byte LocalSlot = NoSlot;

    /// <summary>Session name ที่ใช้เป็น join code</summary>
    public static string SessionCode;

    /// <summary>เครื่องนี้เป็นคนสร้างห้อง (ฝั่ง "host" ของหน้าเลือกตัวละคร / "Player A" ในข้อความแจ้งเตือน)</summary>
    public static bool IsHost;

    /// <summary>ซีนที่กลับไปเมื่อหลุดจากห้อง / คนสร้างห้องออก (แทน LobbySelectionState.initialSceneName)</summary>
    public static string MenuSceneName = "StartScene";

    /// <summary>
    /// เหตุผลที่ถูกพากลับเมนู (key อังกฤษ — แปลตอนโชว์) ให้ <see cref="ConnectionMessagePopup"/> ใน StartScene โชว์กล่องกลางจอ
    /// null = ไม่มี · ShutdownAll ไม่ล้างค่านี้ (ตั้งก่อนออกจากห้อง แล้วอ่านหลังโหลดซีน)
    /// </summary>
    public static string PendingMenuMessage;

    /// <summary>กำลังออกจากห้องเอง (ShutdownAll) — ตัวจับการหลุดจะไม่เด้งกลับเมนูซ้ำ</summary>
    public static bool IsLeaving { get; private set; }

    /// <summary>
    /// ตั้งธงออกจากห้องก่อน Shutdown จริง
    /// ทุกเครื่องต้องตั้งก่อนที่คนสร้างห้องจะปิดห้อง — ไม่งั้น watcher ส่งอีกเครื่องไป StartScene
    /// </summary>
    public static void MarkLeaving() => IsLeaving = true;

    /// <summary>
    /// เทสคนเดียว (FusionQuickTestLauncher → solo) — ห้องรับ 1 คน
    /// ระบบที่ต้อง "ครบสองคน" (ทางออกด่าน, วาร์ป, โซนสไลด์) ให้นับจาก <see cref="CoopPlayerCount"/> แทนเลข 2
    /// </summary>
    public static bool IsSolo;

    /// <summary>จำนวนผู้เล่นที่ต้องครบถึงจะไปต่อ — ปกติ 2, เทส solo = 1</summary>
    public static int CoopPlayerCount => IsSolo ? 1 : 2;

    private const string CodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>join code 6 ตัว (ตัดตัวที่สับสนง่าย 0/O, 1/I ออก)</summary>
    public static string GenerateCode()
    {
        var chars = new char[6];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = CodeChars[UnityEngine.Random.Range(0, CodeChars.Length)];
        return new string(chars);
    }

    /// <summary>
    /// สร้าง/เข้าห้อง Shared Mode ชื่อ = code — runner อยู่ข้ามซีน (DontDestroyOnLoad)
    /// watchConnection = ให้ <see cref="FusionConnectionWatcher"/> พากลับเมนูเมื่อคนสร้างห้องออก/เราหลุด (เมนูจริง)
    /// ล้มเหลว = ทำลาย runner ทิ้งให้แล้ว
    /// </summary>
    public static async Task<StartGameResult> StartSharedAsync(string code, bool allowCreate, int playerCount, bool watchConnection)
    {
        var go = new UnityEngine.GameObject("[Fusion] NetworkRunner");
        UnityEngine.Object.DontDestroyOnLoad(go);
        var runner = go.AddComponent<NetworkRunner>();
        runner.ProvideInput = false;
        var sceneManager = go.AddComponent<NetworkSceneManagerDefault>();

        IsLeaving = false;
        var result = await runner.StartGame(new StartGameArgs
        {
            GameMode = GameMode.Shared,
            SessionName = code,
            PlayerCount = playerCount,
            EnableClientSessionCreation = allowCreate,
            SceneManager = sceneManager,
        });

        if (!result.Ok)
        {
            if (go) UnityEngine.Object.Destroy(go);
            return result;
        }

        SessionCode = code;
        IsHost = allowCreate;
        // ผูกหลังเข้าห้องสำเร็จ — ไม่งั้น StartGame ที่ล้มเหลวจะถูกนับเป็น "หลุด" แล้วเด้งกลับเมนูซ้ำ
        if (watchConnection) runner.AddCallbacks(new FusionConnectionWatcher());
        return result;
    }

    /// <summary>runner ที่กำลังรันอยู่ (มีได้ตัวเดียวในเกมนี้) — null ถ้ายังไม่เข้า session</summary>
    public static Fusion.NetworkRunner ActiveRunner
    {
        get
        {
            foreach (var runner in Fusion.NetworkRunner.Instances)
            {
                if (runner != null && runner.IsRunning) return runner;
            }
            return null;
        }
    }

    /// <summary>เครื่องนี้เป็นคนคุมการโหลดซีน (Shared Mode = master client) — แทน NetworkManager.IsServer ตอนสั่งเปลี่ยนซีน</summary>
    public static bool IsSceneAuthority
    {
        get
        {
            var runner = ActiveRunner;
            return runner != null && runner.IsSceneAuthority;
        }
    }

    /// <summary>
    /// ตัวละครของเครื่องนี้ (แทน NetworkManager.LocalClient.PlayerObject ของ NGO) — null ถ้ายังไม่สปอน
    /// คืนเป็น GameObject เพื่อให้ไฟล์ที่ยังใช้ NGO อยู่เรียกได้โดยไม่ชนชื่อ NetworkObject
    /// </summary>
    public static UnityEngine.GameObject LocalPlayerObject
    {
        get
        {
            foreach (var runner in Fusion.NetworkRunner.Instances)
            {
                if (runner == null || !runner.IsRunning) continue;
                if (runner.TryGetPlayerObject(runner.LocalPlayer, out var obj) && obj != null)
                    return obj.gameObject;
            }
            return null;
        }
    }

    /// <summary>build index ของซีนจากชื่อไฟล์ (-1 = ไม่อยู่ใน Build Settings)</summary>
    public static int GetBuildIndex(string sceneName)
    {
        for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
        {
            var path = SceneUtility.GetScenePathByBuildIndex(i);
            if (Path.GetFileNameWithoutExtension(path) == sceneName) return i;
        }
        return -1;
    }

    /// <summary>
    /// (scene authority) โหลดซีนให้ทุกเครื่องพร้อมกัน — แทน NetworkManager.SceneManager.LoadScene ของ NGO
    /// </summary>
    public static bool LoadSceneNetworked(string sceneName)
    {
        var runner = ActiveRunner;
        if (runner == null || !runner.IsSceneAuthority)
        {
            UnityEngine.Debug.LogError($"[FusionSession] โหลดซีน '{sceneName}' ได้เฉพาะ scene authority (master client)");
            return false;
        }

        int index = GetBuildIndex(sceneName);
        if (index < 0)
        {
            UnityEngine.Debug.LogError($"[FusionSession] ไม่เจอซีน '{sceneName}' ใน Build Settings");
            return false;
        }

        runner.LoadScene(Fusion.SceneRef.FromIndex(index), LoadSceneMode.Single);
        return true;
    }

    /// <summary>
    /// ออกจาก session (ปิด runner ทุกตัว) — แทน NetworkManager.Shutdown ของ NGO
    /// runner ถูกทำลายพร้อม GameObject ของมัน (Shutdown ค่าเริ่มต้น)
    /// </summary>
    public static bool ShutdownAll()
    {
        bool any = false;
        IsLeaving = true;
        // copy ก่อน — Shutdown เอา runner ออกจาก Instances ระหว่างวน
        var runners = new System.Collections.Generic.List<Fusion.NetworkRunner>(Fusion.NetworkRunner.Instances);
        foreach (var runner in runners)
        {
            if (runner == null) continue;
            any = true;
            runner.Shutdown();
        }
        LocalSlot = NoSlot;
        SessionCode = null;
        IsHost = false;
        IsSolo = false;
        return any;
    }

    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        // Domain Reload ปิดอยู่ → ต้องรีเซ็ตเองทุกรอบ Play
        LocalSlot = NoSlot;
        SessionCode = null;
        IsHost = false;
        IsLeaving = false;
        IsSolo = false;
        PendingMenuMessage = null;
    }
}
