using Unity.Netcode;
using UnityEngine;

/// <summary>
/// กระสุนพอร์ทัล — Spawn บน Server, มี Rigidbody + Gravity
/// เมื่อชนวัตถุที่อยู่ใน placeOnMask หรือ PortalSnapPoint จะเปิดพอร์ทัล
/// </summary>
[RequireComponent(typeof(Rigidbody), typeof(Collider), typeof(NetworkObject))]
public class PortalProjectile : NetworkBehaviour
{
    [Header("Lifetime")]
    [Tooltip("เวลาสูงสุด (วินาที) ก่อน auto-despawn ถ้าไม่ชนอะไร")]
    [SerializeField] private float maxLifetime = 5f;

    [Header("Placement")]
    [Tooltip("ระยะ offset จากผนัง (เมตร) เพื่อไม่ให้ portal particle ติดผนัง")]
    [SerializeField] private float portalWallOffset = 0.05f;

    // --- ค่าที่ summoner จะ set ตอน spawn (server เท่านั้น) ---
    private bool _placingA;
    private LayerMask _placeOnMask;
    private bool _initialized;
    private float _spawnTime;
    private PortalSumoner_Net _summoner;

    private Rigidbody _rb;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        // Rigidbody setup — ให้มั่นใจว่า gravity เปิด, ไม่ kinematic
        _rb.useGravity = true;
        _rb.isKinematic = false;
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
    }

    /// <summary>
    /// เรียกจาก PortalSummoner_Net บน Server หลัง Instantiate+Spawn
    /// เพื่อตั้งค่าทิศทาง, ความเร็ว, และข้อมูลที่ต้องใช้ตอนชน
    /// </summary>
    public void InitOnServer(Vector3 velocity, bool placingA, int placeOnMaskValue, PortalSumoner_Net summoner)
    {
        _placingA = placingA;
        _placeOnMask = placeOnMaskValue;
        _summoner = summoner;
        _initialized = true;
        _spawnTime = Time.time;

        _rb.WakeUp();
        _rb.AddForce(velocity, ForceMode.VelocityChange);

        // ส่ง velocity + summoner NetworkObjectId ไปให้ client ทุกเครื่อง
        // เพื่อให้เห็นกระสุนพุ่ง + กัน collision กับผู้เล่นที่ยิงฝั่ง client ด้วย
        ulong summonerNetId = summoner.GetComponent<NetworkObject>().NetworkObjectId;
        ApplyVelocityClientRpc(velocity, summonerNetId);
    }

    [ClientRpc]
    private void ApplyVelocityClientRpc(Vector3 velocity, ulong summonerNetObjId)
    {
        if (IsServer) return; // Server ใส่แล้วข้างบน

        // ★ กัน projectile ชนตัวผู้เล่นที่ยิงฝั่ง Client ด้วย
        // (Physics.IgnoreCollision ใน ServerRpc ทำงานเฉพาะ server)
        var projCol = GetComponent<Collider>();
        if (projCol != null && NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(summonerNetObjId, out var summonerNO))
        {
            foreach (var playerCol in summonerNO.GetComponentsInChildren<Collider>())
            {
                Physics.IgnoreCollision(projCol, playerCol);
            }
        }

        if (_rb == null) _rb = GetComponent<Rigidbody>();
        _rb.WakeUp();
        // ตั้ง velocity ตรงๆ แทน AddForce
        // เพราะระหว่างรอ RPC จาก server, gravity อาจสะสม velocity ลงล่างแล้ว
        _rb.linearVelocity = velocity;
    }

    private void Update()
    {
        // Auto-despawn เฉพาะ server
        if (!IsServer) return;
        if (!_initialized) return;

        if (Time.time - _spawnTime >= maxLifetime)
        {
            DespawnSelf();
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        // ทำงานเฉพาะ Server
        if (!IsServer || !_initialized) return;

        // ไม่ให้ชนตัวผู้เล่นที่ยิง (ปกติ physics ignore ควร handle ได้ แต่กันไว้)
        var hitObj = collision.gameObject;

        // === ตรวจหา PortalSnapPoint ===
        var snapPoint = hitObj.GetComponent<PortalSnapPoint>();
        if (snapPoint == null)
            snapPoint = hitObj.GetComponentInParent<PortalSnapPoint>();

        if (snapPoint != null)
        {
            if (snapPoint.HasSpawnPoint)
            {
                // มี spawnPoint → เปิดพอร์ทัลที่ตำแหน่ง snap
                PlacePortal(snapPoint.SpawnPosition, snapPoint.SpawnRotation);
            }
            else
            {
                // ไม่มี spawnPoint → เปิดพอร์ทัลตรงจุดที่กระสุนชน (เหมือน placement ปกติ)
                ContactPoint contact = collision.GetContact(0);
                Vector3 placePos = contact.point + contact.normal * portalWallOffset;
                Quaternion placeRot = BuildPortalRotationFromNormal(contact.normal);
                PlacePortal(placePos, placeRot);
            }

            NotifySummonerPlaced();
            // เร่ง emission ของ Decal Projector บน snap point (ทุก client)
            NotifySnapPointHitClientRpc(snapPoint.transform.position);
            DespawnSelf();
            return;
        }

        // === เช็ค layer mask ===
        bool isValidLayer = (_placeOnMask.value & (1 << hitObj.layer)) != 0;

        if (isValidLayer)
        {
            // ชน layer ที่ valid → เปิดพอร์ทัลที่จุดสัมผัส
            ContactPoint contact = collision.GetContact(0);
            Vector3 placePos = contact.point + contact.normal * portalWallOffset;
            Quaternion placeRot = BuildPortalRotationFromNormal(contact.normal);

            PlacePortal(placePos, placeRot);
            NotifySummonerPlaced();
        }
        // ไม่ว่า valid หรือไม่ → despawn กระสุน
        DespawnSelf();
    }

    private void PlacePortal(Vector3 pos, Quaternion rot)
    {
        if (PortalPairManager_Net.Instance != null)
        {
            PortalPairManager_Net.Instance.PlacePortalServerRpc(_placingA, pos, rot);
        }
        else
        {
            Debug.LogError("[PortalProjectile] PortalPairManager_Net.Instance == null (ลืมใส่ในซีน?)");
        }
    }

    private void DespawnSelf()
    {
        if (IsSpawned)
        {
            GetComponent<NetworkObject>().Despawn(true);
        }
    }

    /// <summary>
    /// แจ้ง Summoner ว่าพอร์ทัลถูกเปิดสำเร็จ เพื่อสลับ placingA
    /// </summary>
    private void NotifySummonerPlaced()
    {
        if (_summoner != null)
            _summoner.NotifyPortalPlacedClientRpc();
    }

    /// <summary>
    /// แจ้งทุก Client ว่า PortalSnapPoint ถูกยิงโดน เพื่อเล่น emission boost effect
    /// </summary>
    [ClientRpc]
    private void NotifySnapPointHitClientRpc(Vector3 snapPointPos)
    {
        // หา PortalSnapPoint ทุกตัวในซีนแล้วหาตัวที่ตำแหน่งตรงกัน
        var allSnaps = FindObjectsByType<PortalSnapPoint>(FindObjectsSortMode.None);
        foreach (var sp in allSnaps)
        {
            if (Vector3.Distance(sp.transform.position, snapPointPos) <= 0.1f)
            {
                sp.OnHitByProjectile();
                break;
            }
        }
    }

    /// <summary>
    /// สร้าง rotation จาก contact normal (เหมือน PortalSummoner_Net)
    /// </summary>
    private static Quaternion BuildPortalRotationFromNormal(Vector3 surfaceNormal)
    {
        Vector3 forward = -surfaceNormal.normalized;

        Vector3 refAxis = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;

        Vector3 right = Vector3.Cross(refAxis, forward);
        if (right.sqrMagnitude < 1e-6f)
        {
            refAxis = Vector3.right;
            right = Vector3.Cross(refAxis, forward);
        }
        right.Normalize();

        Vector3 up = Vector3.Cross(forward, right).normalized;

        return Quaternion.LookRotation(forward, up);
    }
}
