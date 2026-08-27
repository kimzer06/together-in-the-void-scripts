using UnityEngine;
using DG.Tweening;

[DisallowMultipleComponent]
public class Freezable : MonoBehaviour
{
    [Header("Freeze Options")]
    [SerializeField] private bool freezeRigidbody = true;
    [SerializeField] private bool freezeAnimators = true;
    [SerializeField] private bool includeChildrenAnimators = false;

    [Tooltip("กำหนดเป้าหมาย DOTween ที่จะ Pause/Play (เช่น ใบพัด/ลูกที่หมุน) ถ้าเว้นว่างจะใช้ transform ของอ็อบเจ็กต์นี้")]
    [SerializeField] private Transform[] tweenTargets;

    [Header("Cached (optional)")]
    [SerializeField] private Rigidbody rb;           // ไม่ใส่จะ auto-get
    [SerializeField] private Animator[] animators;   // ไม่ใส่จะ auto-get ตาม includeChildrenAnimators

    // runtime
    private bool isFrozen;
    private Vector3 savedVel, savedAngVel;
    private RigidbodyConstraints savedConstraints;
    private bool savedIsKinematic;
    private TargetHighlight highlight;

    void Reset()
    {
        rb = GetComponent<Rigidbody>();
        // อย่าดึงทั้ง children ตอน Reset เพื่อเลี่ยงไปแช่ของที่ไม่เกี่ยว
        animators = GetComponents<Animator>();
    }

    void Awake()
    {
        if (!rb) rb = GetComponent<Rigidbody>();

        if (freezeAnimators)
        {
            if (animators == null || animators.Length == 0)
            {
                animators = includeChildrenAnimators
                    ? GetComponentsInChildren<Animator>(true)
                    : GetComponents<Animator>();
            }
        }

        highlight = GetComponentInChildren<TargetHighlight>(true);

        // ถ้าไม่กำหนด tweenTargets เลย ให้ใช้ตัวเองเป็นขั้นต่ำ
        if (tweenTargets == null || tweenTargets.Length == 0)
            tweenTargets = new Transform[] { transform };
    }

    public bool IsFrozen => isFrozen;

    public void FreezeOn()
    {
        if (isFrozen) return;
        isFrozen = true;

        SfxManager.PlayFreeze(transform.position);

        if (highlight) highlight.SetForced(true);

        if (freezeRigidbody && rb)
        {
            savedVel = rb.linearVelocity;
            savedAngVel = rb.angularVelocity;
            savedConstraints = rb.constraints;
            savedIsKinematic = rb.isKinematic;
            
            // ใช้ isKinematic = true เพื่อให้วัตถุไม่สามารถถูกดันได้เลย ไม่ว่า mass จะเป็นเท่าไหร่
            rb.isKinematic = true;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        if (freezeAnimators && animators != null)
        {
            foreach (var a in animators) if (a) a.speed = 0f;
        }

        // DOTween: Pause เฉพาะเป้าหมายที่ระบุ
        foreach (var t in tweenTargets)
        {
            if (!t) continue;
            DOTween.Pause(t);
            DOTween.Pause(t.gameObject);
        }
    }

    public void FreezeOff()
    {
        if (!isFrozen) return;

        if (freezeRigidbody && rb)
        {
            rb.isKinematic = savedIsKinematic;
            rb.constraints = savedConstraints;
            rb.linearVelocity = savedVel;
            rb.angularVelocity = savedAngVel;
        }

        if (freezeAnimators && animators != null)
        {
            foreach (var a in animators) if (a) a.speed = 1f;
        }

        // DOTween: Resume เฉพาะเป้าหมายที่ระบุ
        foreach (var t in tweenTargets)
        {
            if (!t) continue;
            DOTween.Play(t);
            DOTween.Play(t.gameObject);
        }

        if (highlight)
        {
            highlight.SetForced(false);
            highlight.ForceClearAll();
        }

        isFrozen = false;
    }

    /// <summary>ตั้งเป้าหมาย DOTween ใหม่ (ถ้าต้องการสลับ runtime)</summary>
    public void SetTweenTargets(params Transform[] targets)
    {
        tweenTargets = targets;
    }
}