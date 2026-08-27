using UnityEngine;
using UnityEngine.UI;
using System.Collections;

public class ScreenFader : MonoBehaviour
{
    public static ScreenFader I { get; private set; }

    [SerializeField] private float defaultFadeDuration = 0.75f;

    Canvas _canvas;
    Image _black;
    Coroutine _co;

    void Awake()
    {
        if (I && I != this) { Destroy(gameObject); return; }
        I = this;
        DontDestroyOnLoad(gameObject);

        // สร้าง Canvas + Image ทับเต็มหน้าจอแบบโปรแกรมmatically
        _canvas = new GameObject("FadeCanvas").AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = short.MaxValue; // ให้อยู่บนสุด
        _canvas.gameObject.transform.SetParent(transform);

        var ray = _canvas.gameObject.AddComponent<GraphicRaycaster>();
        ray.blockingObjects = GraphicRaycaster.BlockingObjects.None;

        _black = new GameObject("FadeImage").AddComponent<Image>();
        _black.color = new Color(0,0,0,0);
        _black.raycastTarget = false;
        _black.rectTransform.SetParent(_canvas.transform, false);
        _black.rectTransform.anchorMin = Vector2.zero;
        _black.rectTransform.anchorMax = Vector2.one;
        _black.rectTransform.offsetMin = Vector2.zero;
        _black.rectTransform.offsetMax = Vector2.zero;
    }

    public void InstantBlack()
    {
        if (_co != null) StopCoroutine(_co);
        SetAlpha(1f);
    }

    public void InstantClear()
    {
        if (_co != null) StopCoroutine(_co);
        SetAlpha(0f);
    }

    public void FadeOut(float duration = -1f) // ไปดำ
    {
        if (duration <= 0) duration = defaultFadeDuration;
        if (_co != null) StopCoroutine(_co);
        _co = StartCoroutine(FadeRoutine(targetAlpha: 1f, duration));
    }

    public void FadeIn(float duration = -1f) // จากดำออก
    {
        if (duration <= 0) duration = defaultFadeDuration;
        if (_co != null) StopCoroutine(_co);
        _co = StartCoroutine(FadeRoutine(targetAlpha: 0f, duration));
    }

    IEnumerator FadeRoutine(float targetAlpha, float dur)
    {
        float start = _black.color.a;
        float t = 0f;
        while (t < dur)
        {
            t += Time.unscaledDeltaTime;
            float a = Mathf.Lerp(start, targetAlpha, t / dur);
            SetAlpha(a);
            yield return null;
        }
        SetAlpha(targetAlpha);
        _co = null;
    }

    void SetAlpha(float a)
    {
        var c = _black.color;
        c.a = a;
        _black.color = c;
    }
}
