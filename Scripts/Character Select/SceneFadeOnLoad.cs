using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;

// ติดไว้ในแต่ละซีน (GameObject ว่างตัวหนึ่งก็พอ)
public class SceneFadeOnLoad : MonoBehaviour
{
    [SerializeField] private float fadeInDuration = 0.8f;

    void OnEnable()
    {
        // เผื่อเข้าซีนด้วยวิธีอื่น ก็ให้ Clear ดำก่อนแล้วค่อยเฟดออกเนียนๆ
        if (ScreenFader.I != null)
        {
            ScreenFader.I.InstantBlack();    // ขึ้นซีนมาจะเป็นดำก่อน
            ScreenFader.I.FadeIn(fadeInDuration);
        }
    }
}
