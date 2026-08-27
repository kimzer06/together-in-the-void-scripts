using UnityEngine;
using Unity.Netcode;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Spawner ที่ทำงานเมื่อได้รับสัญญาณจาก InteractSwitch (implement IActivatable)
/// หรือเมื่อ Player เข้า Collider Trigger ที่เลือกไว้ใน Inspector
/// รองรับ Pooling, Cadence Loop, และ RollingBoulder configuration
/// </summary>
[DisallowMultipleComponent]
public class SpawnerOnSwitch : NetworkBehaviour, IActivatable
{
    public enum Axis { X, Y, Z }
    public enum Direction { Positive, Negative }

    public enum SpawnTriggerMode
    {
        [Tooltip("Spawn เมื่อ InteractSwitch เรียก Activate (IActivatable)")]
        Switch,
        [Tooltip("Spawn เมื่อ Player เข้า Collider Trigger ที่เลือก")]
        ColliderTrigger
    }

    [Header("Trigger Mode")]
    [SerializeField] private SpawnTriggerMode triggerMode = SpawnTriggerMode.Switch;

    [Tooltip("Collider (ต้องเป็น isTrigger) ที่จะใช้ตรวจจับ Player — ใช้เมื่อ triggerMode = ColliderTrigger")]
    [SerializeField] private Collider triggerCollider;

    [Tooltip("Tag ของ object ที่จะ trigger spawn (default = Player)")]
    [SerializeField] private string triggerTag = "Player";

    [Tooltip("ถ้าเปิด จะ spawn ได้แค่ครั้งเดียวเมื่อโดน trigger (ใช้เมื่อ triggerMode = ColliderTrigger)")]
    [SerializeField] private bool triggerOnce = true;

    [Header("Spawn")]
    [SerializeField] private NetworkObject prefab;
    [SerializeField] private Transform spawnPoint;
    [SerializeField] private Axis axis = Axis.X;
    [SerializeField] private Direction direction = Direction.Positive;

    [Header("Spawning Mode")]
    [Tooltip("ถ้าเปิด = Spawn ครั้งเดียวเมื่อสวิตช์เปิด, ถ้าปิด = Spawn ต่อเนื่องทุก cooldown")]
    [SerializeField] private bool spawnOnceOnly = true;
    [Tooltip("สปอนทุกกี่วินาที (ใช้เมื่อ spawnOnceOnly = false)")]
    [SerializeField, Min(0.05f)] private float cooldown = 2f;
    [Tooltip("จำนวนที่จะ spawn ต่อครั้ง")]
    [SerializeField, Min(1)] private int spawnCount = 1;

    [Header("Pooling")]
    [SerializeField] private bool usePooling = true;
    [Tooltip("จำนวนที่อุ่นไว้ในพูลตอนเริ่ม")]
    [SerializeField, Min(0)] private int prewarmCount = 5;

    [Header("RollingBoulder Config (Optional)")]
    [Tooltip("ถ้า prefab มี RollingBoulder จะ configure axis/direction ให้อัตโนมัติ")]
    [SerializeField] private bool configureRollingBoulder = true;

    private readonly Queue<NetworkObject> pool = new();
    private bool isActive = false;
    private Coroutine loopCo;
    private bool hasTriggered = false;

    /// <summary>
    /// IActivatable implementation - ถูกเรียกจาก InteractSwitch
    /// </summary>
    public void Activate(bool on)
    {
        if (!IsServer) return;

        if (on)
        {
            StartSpawning();
        }
        else
        {
            StopSpawning();
        }
    }

    private void StartSpawning()
    {
        if (!IsServer) return;

        if (spawnOnceOnly)
        {
            // Spawn ครั้งเดียว
            for (int i = 0; i < spawnCount; i++)
            {
                SpawnOne();
            }
        }
        else
        {
            // Spawn ต่อเนื่อง
            if (!isActive)
            {
                isActive = true;
                loopCo = StartCoroutine(LoopSpawnEveryCooldown());
            }
        }
    }

    private void StopSpawning()
    {
        if (!IsServer) return;

        if (loopCo != null)
        {
            StopCoroutine(loopCo);
            loopCo = null;
        }
        isActive = false;
    }

    private IEnumerator LoopSpawnEveryCooldown()
    {
        // Spawn รอบแรกทันที
        for (int i = 0; i < spawnCount; i++)
        {
            SpawnOne();
        }

        var wait = new WaitForSeconds(cooldown);
        while (true)
        {
            yield return wait;
            for (int i = 0; i < spawnCount; i++)
            {
                SpawnOne();
            }
        }
    }

    private void SpawnOne()
    {
        if (!IsServer || !prefab || !spawnPoint) return;

        NetworkObject no;
        if (usePooling && pool.Count > 0)
        {
            no = pool.Dequeue();
            no.transform.SetPositionAndRotation(spawnPoint.position, spawnPoint.rotation);

            ConfigureBoulder(no);

            no.gameObject.SetActive(true);
            no.Spawn();
        }
        else
        {
            no = Instantiate(prefab, spawnPoint.position, spawnPoint.rotation);
            ConfigureBoulder(no);
            no.Spawn();
        }

        // Subscribe to despawn event for pooling
        var rock = no.GetComponent<RollingBoulder>();
        if (rock != null)
        {
            rock.ServerDespawned -= OnObjectDespawned;
            rock.ServerDespawned += OnObjectDespawned;
        }
    }

    private void ConfigureBoulder(NetworkObject no)
    {
        if (!configureRollingBoulder) return;

        var rockCfg = no.GetComponent<RollingBoulder>();
        if (rockCfg != null)
        {
            rockCfg.Configure((RollingBoulder.Axis)axis, (RollingBoulder.Direction)direction);
            rockCfg.returnToPool = usePooling;
        }
    }

    private void OnObjectDespawned(RollingBoulder rock)
    {
        if (!IsServer || rock == null) return;
        rock.ServerDespawned -= OnObjectDespawned;

        if (usePooling)
        {
            var no = rock.NetworkObject;
            if (no != null)
            {
                no.gameObject.SetActive(false);
                pool.Enqueue(no);
            }
        }
    }

    // ──────────────────────────────────────
    // Collider Trigger Mode — Setup
    // ──────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        // Prewarm pool
        if (usePooling && prefab != null)
        {
            for (int i = 0; i < prewarmCount; i++)
            {
                var no = Instantiate(prefab);
                no.gameObject.SetActive(false);
                pool.Enqueue(no);
            }
        }

        // ถ้าเป็นโหมด ColliderTrigger ให้ติด helper ไว้บน Collider ที่เลือก
        if (triggerMode == SpawnTriggerMode.ColliderTrigger && triggerCollider != null)
        {
            var relay = triggerCollider.gameObject.AddComponent<SpawnerTriggerRelay>();
            relay.Init(this, triggerTag);
        }
    }

    /// <summary>
    /// ถูกเรียกจาก SpawnerTriggerRelay เมื่อ Player เข้า trigger
    /// </summary>
    public void OnTriggerActivated()
    {
        if (!IsServer) return;
        if (triggerOnce && hasTriggered) return;

        hasTriggered = true;
        StartSpawning();
    }

    private void OnDisable()
    {
        StopSpawning();
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!spawnPoint) return;

        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(spawnPoint.position, 0.25f);

        Vector3 dir = axis == Axis.X ? Vector3.right :
                      axis == Axis.Y ? Vector3.up :
                                       Vector3.forward;
        if (direction == Direction.Negative) dir = -dir;

        Gizmos.color = Color.magenta;
        Gizmos.DrawLine(spawnPoint.position, spawnPoint.position + dir * 2f);
        UnityEditor.Handles.ArrowHandleCap(0, spawnPoint.position, Quaternion.LookRotation(dir), 1.2f, EventType.Repaint);

        // วาด Gizmo ที่ trigger collider ด้วย
        if (triggerMode == SpawnTriggerMode.ColliderTrigger && triggerCollider != null)
        {
            Gizmos.color = new Color(0f, 1f, 0.5f, 0.4f);
            Gizmos.DrawWireCube(triggerCollider.bounds.center, triggerCollider.bounds.size);
        }
    }
#endif
}

/// <summary>
/// Helper ที่ถูกเพิ่มลงบน Collider Trigger ตอน runtime
/// เพื่อส่งต่อ OnTriggerEnter กลับไปยัง SpawnerOnSwitch
/// </summary>
public class SpawnerTriggerRelay : MonoBehaviour
{
    private SpawnerOnSwitch spawner;
    private string requiredTag;

    public void Init(SpawnerOnSwitch owner, string tag)
    {
        spawner = owner;
        requiredTag = tag;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (spawner == null) return;
        if (!string.IsNullOrEmpty(requiredTag) && !other.CompareTag(requiredTag)) return;

        spawner.OnTriggerActivated();
    }
}

