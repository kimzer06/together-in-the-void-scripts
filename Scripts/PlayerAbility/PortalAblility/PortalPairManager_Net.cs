// PortalPairManager_Net.cs
using Unity.Netcode;
using UnityEngine;
using System.Collections;

public class PortalPairManager_Net : NetworkBehaviour
{
    public static PortalPairManager_Net Instance { get; private set; }

    [Header("Prefab ที่จะ Spawn บนเซิร์ฟเวอร์")]
    [SerializeField] private GameObject portalPrefabA;   // Prefab สำหรับ Portal A
    [SerializeField] private GameObject portalPrefabB;   // Prefab สำหรับ Portal B

    [Header("Close Animation")]
    [Tooltip("ระยะเวลา (วินาที) ที่รอ animation PortalClose ก่อน Despawn")]
    [SerializeField] private float closeAnimDuration = 0.5f;

    // อ้างอิงพอร์ทัลที่ spawn อยู่ปัจจุบัน (ให้ทั้งเกมมีได้คู่เดียว)
    private NetworkVariable<NetworkObjectReference> _portalARef = new();
    private NetworkVariable<NetworkObjectReference> _portalBRef = new();

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    public bool TryGetPortal(bool isA, out Portal_Net portal)
    {
        portal = null;
        var r = isA ? _portalARef.Value : _portalBRef.Value;
        if (r.TryGet(out var no)) portal = no.GetComponent<Portal_Net>();
        return portal != null;
    }

    public bool IsPairReady =>
        _portalARef.Value.TryGet(out var _a) && _portalBRef.Value.TryGet(out var _b);

    // ========== API ฝั่ง Client เรียกผ่าน RPC ==========
    [ServerRpc(RequireOwnership = false)]
    public void PlacePortalServerRpc(bool isA, Vector3 pos, Quaternion rot)
    {
        // ไม่มีพอร์ทัล? สร้างใหม่
        if (!TryGetPortal(isA, out var portal))
        {
            // เลือก prefab ตาม isA
            var prefab = isA ? portalPrefabA : portalPrefabB;
            if (prefab == null)
            {
                Debug.LogError($"[PortalPairManager] portalPrefab{(isA ? "A" : "B")} ยังไม่ได้ตั้ง!");
                return;
            }

            var go = Instantiate(prefab, pos, rot);
            var no = go.GetComponent<NetworkObject>();
            no.Spawn(true);
            portal = go.GetComponent<Portal_Net>();
            portal.SetIsA(isA);

            if (isA) _portalARef.Value = no;
            else _portalBRef.Value = no;
        }
        else
        {
            // ปิดวาร์ปทันที — ตัด pair ออก
            portal.SetPair(null);
            // ถ้าอีกฝั่งมี pair ก็ตัดด้วย
            if (TryGetPortal(!isA, out var otherPortal))
                otherPortal.SetPair(null);

            // เล่น PortalClose บนตัวเก่า + despawn หลัง animation จบ
            var oldNO = portal.GetComponent<NetworkObject>();
            if (oldNO != null && oldNO.IsSpawned)
            {
                PlayCloseAndDespawnClientRpc(oldNO.NetworkObjectId);
                TriggerCloseAnim(oldNO);
                StartCoroutine(DespawnAfterDelay(oldNO, closeAnimDuration));
            }

            // ลบ ref ทันที เพื่อให้ spawn ตัวใหม่ได้
            if (isA) _portalARef.Value = default;
            else _portalBRef.Value = default;

            // Spawn ตัวใหม่ทันที
            var prefab = isA ? portalPrefabA : portalPrefabB;
            if (prefab == null) return;

            var go = Instantiate(prefab, pos, rot);
            var no = go.GetComponent<NetworkObject>();
            no.Spawn(true);
            var newPortal = go.GetComponent<Portal_Net>();
            newPortal.SetIsA(isA);

            if (isA) _portalARef.Value = no;
            else _portalBRef.Value = no;
        }

        // ผูกคู่ให้กันและกัน (ถ้าอีกฝั่งพร้อม)
        if (IsPairReady)
        {
            var Aok = _portalARef.Value.TryGet(out var A);
            var Bok = _portalBRef.Value.TryGet(out var B);
            if (Aok && Bok)
            {
                var pa = A.GetComponent<Portal_Net>();
                var pb = B.GetComponent<Portal_Net>();
                pa.SetPair(pb);
                pb.SetPair(pa);
            }
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void ResetPairServerRpc()
    {
        StartCoroutine(CloseAndDespawnRoutine());
    }

    private IEnumerator CloseAndDespawnRoutine()
    {
        bool hasA = _portalARef.Value.TryGet(out var A);
        bool hasB = _portalBRef.Value.TryGet(out var B);

        // เล่น PortalClose animation บนทุก client
        if (hasA || hasB)
        {
            PlayCloseAnimClientRpc(
                hasA ? A.NetworkObjectId : 0,
                hasB ? B.NetworkObjectId : 0,
                hasA, hasB
            );

            // เล่นบน Server ด้วย
            if (hasA) TriggerCloseAnim(A);
            if (hasB) TriggerCloseAnim(B);

            // รอ animation จบ
            yield return new WaitForSeconds(closeAnimDuration);
        }

        // Despawn
        if (hasA && A != null && A.IsSpawned) A.Despawn();
        if (hasB && B != null && B.IsSpawned) B.Despawn();
        _portalARef.Value = default;
        _portalBRef.Value = default;
    }

    [ClientRpc]
    private void PlayCloseAnimClientRpc(ulong portalAId, ulong portalBId, bool hasA, bool hasB)
    {
        if (IsServer) return; // Server เล่นเองแล้ว

        var sm = NetworkManager.Singleton.SpawnManager;
        if (hasA && sm.SpawnedObjects.TryGetValue(portalAId, out var A))
            TriggerCloseAnim(A);
        if (hasB && sm.SpawnedObjects.TryGetValue(portalBId, out var B))
            TriggerCloseAnim(B);
    }

    private void TriggerCloseAnim(NetworkObject portalNO)
    {
        var animator = portalNO.GetComponentInChildren<Animator>();
        if (animator != null)
            animator.SetTrigger("PortalClose");
    }

    /// <summary>
    /// เล่น PortalClose บน client สำหรับ portal ตัวเดียว (ใช้ตอน reposition)
    /// </summary>
    [ClientRpc]
    private void PlayCloseAndDespawnClientRpc(ulong portalId)
    {
        if (IsServer) return; // Server เล่นเองแล้ว

        var sm = NetworkManager.Singleton.SpawnManager;
        if (sm.SpawnedObjects.TryGetValue(portalId, out var portalNO))
            TriggerCloseAnim(portalNO);
    }

    private IEnumerator DespawnAfterDelay(NetworkObject no, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (no != null && no.IsSpawned)
            no.Despawn();
    }

    /// <summary>
    /// ใช้สำหรับการบังคับลบพอร์ทัลทันที (แบบไม่เล่นอนิเมชัน PortalClose)
    /// นิยมใช้เมื่อ Platform / SnapPoint ขยับ
    /// จะเล่น Particle (ถ้ามี) บนทุก Client ก่อน Despawn
    /// </summary>
    public void ForceDespawnPortal(bool isA)
    {
        if (!IsServer) return;

        if (TryGetPortal(isA, out var portal))
        {
            Vector3 portalPos = portal.transform.position;
            Quaternion portalRot = portal.transform.rotation;

            // ตัดคู่
            portal.SetPair(null);
            if (TryGetPortal(!isA, out var otherPortal))
            {
                otherPortal.SetPair(null);
            }

            // เล่น Particle บนทุก Client + Server ก่อน Despawn
            if (despawnParticlePrefab != null)
            {
                // ยิง ClientRpc ไปเล่นให้ Client ทุกเครื่อง
                SpawnParticleClientRpc(portalPos, portalRot);

                // Server เล่นเอง
                var ps = Instantiate(despawnParticlePrefab, portalPos, portalRot);
                ps.Play();
                Destroy(ps.gameObject, ps.main.duration + ps.main.startLifetime.constantMax);
            }

            // Despawn ตัวนี้ทันที
            var no = portal.GetComponent<NetworkObject>();
            if (no != null && no.IsSpawned)
            {
                no.Despawn(true);
            }

            // เคลียร์ ref
            if (isA) _portalARef.Value = default;
            else _portalBRef.Value = default;
        }
    }

    [Header("Force Despawn Particle")]
    [Tooltip("Prefab ของ Particle ที่จะเล่นบนทุก Client เมื่อพอร์ทัลถูก Force Despawn (เช่น จากการขยับ SnapPoint)")]
    [SerializeField] private ParticleSystem despawnParticlePrefab;

    [ClientRpc]
    private void SpawnParticleClientRpc(Vector3 position, Quaternion rotation)
    {
        if (IsServer) return; // Server เล่นเองแล้ว
        if (despawnParticlePrefab == null) return;

        var ps = Instantiate(despawnParticlePrefab, position, rotation);
        ps.Play();
        Destroy(ps.gameObject, ps.main.duration + ps.main.startLifetime.constantMax);
    }
}