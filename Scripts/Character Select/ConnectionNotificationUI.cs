using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;
using DG.Tweening;
using TMPro;

/// <summary>
/// แสดง toast notification เมื่อผู้เล่นเข้า/ออกจากห้อง
/// วางลงบน GameObject ไหนก็ได้ใน scene (ไม่ต้อง setup Prefab)
/// สร้าง Canvas + Text ด้วยโค้ดอัตโนมัติ
/// </summary>
public class ConnectionNotificationUI : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private float displayDuration = 2.5f;
    [SerializeField] private float fadeDuration = 0.4f;
    [SerializeField] private int fontSize = 28;
    [SerializeField] private float topPadding = 40f;
    [SerializeField] private float spacing = 50f;

    private Canvas notifCanvas;
    private int activeCount;

    private void Start()
    {
        if (NetworkManager.Singleton == null) return;

        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
    }

    private void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        }

        if (notifCanvas) Destroy(notifCanvas.gameObject);
    }

    // ───────── Callbacks ─────────

    private void OnClientConnected(ulong clientId)
    {
        bool isHost = clientId == NetworkManager.ServerClientId;
        string playerName = isHost ? "Player A" : "Player B";

        // ฝั่ง client ไม่ต้องแจ้ง "Player A joined!" (host เชื่อมก่อนอยู่แล้ว)
        if (isHost && clientId != NetworkManager.Singleton.LocalClientId) return;

        ShowNotification($"{playerName} joined!", new Color(0.2f, 0.9f, 0.4f));
    }

    private void OnClientDisconnected(ulong clientId)
    {
        // ไม่แจ้งตอนตัวเองหลุด
        if (clientId == NetworkManager.Singleton.LocalClientId) return;

        bool isHost = clientId == NetworkManager.ServerClientId;
        string playerName = isHost ? "Player A" : "Player B";

        ShowNotification($"{playerName} left...", new Color(1f, 0.35f, 0.35f));
    }

    // ───────── Create UI At Runtime ─────────

    /// <summary>
    /// เรียกจากที่ไหนก็ได้ (static) เพื่อแสดง notification แม้ตอน scene กำลังโหลดใหม่
    /// Canvas จะ DontDestroyOnLoad และทำลายตัวเองหลัง notification จบ
    /// </summary>
    public static void ShowGlobal(string message, Color color,
                                   float display = 2.5f, float fade = 0.4f, int size = 28)
    {
        // สร้าง Canvas ชั่วคราวใหม่ทุกครั้ง (เพราะ instance อาจถูกทำลายแล้ว)
        var canvasGO = new GameObject("GlobalNotifCanvas");
        Object.DontDestroyOnLoad(canvasGO);

        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 999;

        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        canvasGO.AddComponent<GraphicRaycaster>();

        CreateToast(canvasGO.transform, message, color, 0, display, fade, size, () =>
        {
            Object.Destroy(canvasGO);
        });
    }

    private void EnsureCanvas()
    {
        if (notifCanvas) return;

        // สร้าง Canvas แยกเป็น overlay เพื่อไม่ชนกับ Canvas อื่น
        var canvasGO = new GameObject("NotificationCanvas");
        DontDestroyOnLoad(canvasGO);

        notifCanvas = canvasGO.AddComponent<Canvas>();
        notifCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        notifCanvas.sortingOrder = 999; // อยู่บนสุด

        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        canvasGO.AddComponent<GraphicRaycaster>();
    }

    private void ShowNotification(string message, Color color)
    {
        EnsureCanvas();

        CreateToast(notifCanvas.transform, message, color, activeCount,
                    displayDuration, fadeDuration, fontSize, null);
        activeCount++;
    }

    // ───────── Shared Toast Factory (static) ─────────

    private static void CreateToast(Transform parent, string message, Color color,
                                     int stackIndex, float display, float fade, int size,
                                     System.Action onComplete)
    {
        // สร้าง background panel
        var panelGO = new GameObject("NotifPanel");
        panelGO.transform.SetParent(parent, false);

        var panelRect = panelGO.AddComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0.5f, 1f);
        panelRect.anchorMax = new Vector2(0.5f, 1f);
        panelRect.pivot = new Vector2(0.5f, 1f);

        float yPos = -(40f + stackIndex * 50f);
        panelRect.anchoredPosition = new Vector2(0, yPos);
        panelRect.sizeDelta = new Vector2(420, 44);

        var panelImg = panelGO.AddComponent<Image>();
        panelImg.color = new Color(0, 0, 0, 0.7f);

        // สร้าง Text
        var textGO = new GameObject("NotifText");
        textGO.transform.SetParent(panelGO.transform, false);

        var textRect = textGO.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(16, 0);
        textRect.offsetMax = new Vector2(-16, 0);

        var tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text = message;
        tmp.fontSize = size;
        tmp.color = color;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableAutoSizing = false;

        // เริ่มโปร่งใส
        var cg = panelGO.AddComponent<CanvasGroup>();
        cg.alpha = 0f;

        // Animation: fade in → hold → fade out → destroy
        Sequence seq = DOTween.Sequence();
        seq.Append(cg.DOFade(1f, fade).SetEase(Ease.OutCubic));
        seq.AppendInterval(display);
        seq.Append(cg.DOFade(0f, fade).SetEase(Ease.InCubic));
        seq.OnComplete(() =>
        {
            Object.Destroy(panelGO);
            onComplete?.Invoke();
        });
    }
}
