using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// เทสคนเดียวจากซีนที่เปิดอยู่ — ไม่ต้องเปิด MPPM Player 2
/// กด Play โดยเริ่มจากซีน FusionQuickTest แทน (playModeStartScene) แล้ว launcher สั่ง solo เข้าซีนเดิมให้เอง
/// ออก Play แล้วคืนค่า playModeStartScene — กด Play ปกติยังเหมือนเดิม
/// </summary>
[InitializeOnLoad]
public static class SoloPlayMenu
{
    private const string QuickTestScenePath = "Assets/_Together In the void/Scenes/Main/FusionQuickTest.unity";
    private const string OverrideActiveKey = "Together.SoloPlay.OverrideActive";

    static SoloPlayMenu()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    [MenuItem("Tools/Together/Solo Play/Current Scene as Lyra (A) &F5", priority = 0)]
    private static void PlayAsLyra() => Launch(asPlayerB: false);

    [MenuItem("Tools/Together/Solo Play/Current Scene as Bobby (B) &F6", priority = 1)]
    private static void PlayAsBobby() => Launch(asPlayerB: true);

    [MenuItem("Tools/Together/Solo Play/Current Scene as Lyra (A) &F5", validate = true)]
    [MenuItem("Tools/Together/Solo Play/Current Scene as Bobby (B) &F6", validate = true)]
    private static bool CanLaunch() => !EditorApplication.isPlayingOrWillChangePlaymode;

    private static void Launch(bool asPlayerB)
    {
        var scene = SceneManager.GetActiveScene();
        if (string.IsNullOrEmpty(scene.path))
        {
            EditorUtility.DisplayDialog("Solo Play", "เซฟซีนก่อน — ซีนนี้ยังไม่มีไฟล์", "OK");
            return;
        }

        if (!IsInBuildSettings(scene.path))
        {
            EditorUtility.DisplayDialog("Solo Play",
                $"ซีน '{scene.name}' ไม่อยู่ใน Build Settings (หรือถูกปิดไว้)\nFusion โหลดซีนด้วย build index — เพิ่มเข้า Build Settings ก่อน",
                "OK");
            return;
        }

        // Fusion โหลดซีนจากไฟล์ — ของที่ยังไม่เซฟจะไม่ติดไปด้วย
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        var quickTest = AssetDatabase.LoadAssetAtPath<SceneAsset>(QuickTestScenePath);
        if (quickTest == null)
        {
            Debug.LogError($"[SoloPlay] ไม่เจอซีน {QuickTestScenePath}");
            return;
        }

        SessionState.SetString(FusionQuickTestLauncher.EditorSoloSceneKey, scene.name);
        SessionState.SetBool(FusionQuickTestLauncher.EditorSoloAsPlayerBKey, asPlayerB);
        SessionState.SetBool(OverrideActiveKey, true);
        EditorSceneManager.playModeStartScene = quickTest;
        EditorApplication.isPlaying = true;
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode) return;
        if (!SessionState.GetBool(OverrideActiveKey, false)) return;

        // คืนค่าให้กด Play ปกติเริ่มจากซีนที่เปิดอยู่เหมือนเดิม + ล้างค่าที่ launcher อาจยังไม่ได้ใช้
        EditorSceneManager.playModeStartScene = null;
        SessionState.EraseBool(OverrideActiveKey);
        SessionState.EraseString(FusionQuickTestLauncher.EditorSoloSceneKey);
        SessionState.EraseBool(FusionQuickTestLauncher.EditorSoloAsPlayerBKey);
    }

    private static bool IsInBuildSettings(string scenePath)
    {
        foreach (var s in EditorBuildSettings.scenes)
            if (s.enabled && s.path == scenePath) return true;
        return false;
    }
}
