using UnityEngine;

[RequireComponent(typeof(Collider))]
public class CheckpointTrigger : MonoBehaviour
{
    /// <summary>เช็คพ้อยท์ล่าสุดในระดับเกม — ถ้าเข้าใหม่จะปิดตัวก่อนหน้าเพื่อกันเซฟย้อนกลับ</summary>
    private static CheckpointTrigger s_currentCheckpoint;

    [Header("Checkpoint Settings")]
    [Tooltip("จุดเกิดใหม่ที่ผู้เล่นจะถูกส่งไปหลังตาย")]
    [SerializeField] private Transform newSpawnPoint;

    [Tooltip("Tag ของผู้เล่นที่ต้องการให้ตรวจจับ")]
    [SerializeField] private string playerTag = "Player";

    [Header("Visual Feedback (Optional)")]
    [Tooltip("เอฟเฟกต์ที่จะเล่นเมื่อผู้เล่นแตะเช็คพ้อยท์")]
    [SerializeField] private ParticleSystem activationEffect;
    
    // private bool hasBeenTriggered = false; // <--- ลบบรรทัดนี้ออก

    private void Awake()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    private void OnDestroy()
    {
        if (s_currentCheckpoint == this)
        {
            s_currentCheckpoint = null;
        }
    }

    private void DisableAsSuperseded()
    {
        var col = GetComponent<Collider>();
        if (col != null)
        {
            col.enabled = false;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        // แก้เงื่อนไขให้เช็คแค่ Tag ก็พอ
        if (!other.CompareTag(playerTag))
        {
            return;
        }

        var playerDeath = other.GetComponent<PlayerDeath>();
        if (playerDeath != null)
        {
            if (s_currentCheckpoint != null && s_currentCheckpoint != this)
            {
                s_currentCheckpoint.DisableAsSuperseded();
            }

            s_currentCheckpoint = this;

            playerDeath.SetRespawnPoint(newSpawnPoint);
            Debug.Log($"ผู้เล่น '{other.name}' ได้บันทึกจุดเกิดใหม่ที่ '{newSpawnPoint.name}'", gameObject);

            if (activationEffect != null)
            {
                // อาจจะเพิ่มเงื่อนไขเช็คว่า particle system กำลังเล่นอยู่หรือไม่
                // เพื่อไม่ให้เล่นซ้อนกันถี่ๆ
                if (!activationEffect.isPlaying)
                {
                    activationEffect.Play();
                }
            }
            
            // hasBeenTriggered = true; // <--- ลบบรรทัดนี้ออก
        }
    }

    // ... ส่วน OnDrawGizmos เหมือนเดิม ...
#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (newSpawnPoint != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawLine(transform.position, newSpawnPoint.position);
            Gizmos.DrawWireSphere(newSpawnPoint.position, 0.5f);
            Gizmos.DrawIcon(newSpawnPoint.position + Vector3.up * 1.5f, "checkpoint_icon.png", true);
        }
    }
#endif
}