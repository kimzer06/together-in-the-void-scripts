using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components; // สำหรับ NetworkTransform / ClientNetworkTransform
using UnityEngine.Audio;

[RequireComponent(typeof(Collider)), RequireComponent(typeof(NetworkObject))]
[DisallowMultipleComponent]
public class Portal_Net : NetworkBehaviour
{
    [Header("Setup")]
    [SerializeField] private bool isA = true;
    [SerializeField] private Transform exitOffset; // ว่าง = ใช้ transform เอง
    
    [Header("Audio (Open/Close)")]
    [Tooltip("AudioSource ที่ตั้งไว้ในซีน/พรีแฟบเอง (สคริปต์นี้จะไม่สร้างให้)")]
    [SerializeField] private AudioSource portalAudioSource;
    [SerializeField] private AudioMixerGroup sfxMixerGroup;
    [SerializeField] private AudioClip openSound;
    [SerializeField] private AudioClip closeSound;
    [Range(0f, 1f)][SerializeField] private float portalSfxVolume = 0.9f;

    [Header("Teleport Tuning")]
    [Tooltip("บล็อกการเข้าใหม่ชั่วคราวหลังวาร์ป (ต่อผู้เล่น/วัตถุ)")]
    [SerializeField] private float reenterCooldown = 0.45f;
    [SerializeField] private float exitForwardPush = 1.0f;
    [SerializeField] private float exitUpPush = 0.05f;
    [SerializeField] private bool zeroVelocityOnTeleport = true; // แนะนำ false สำหรับ Boulder

    [Header("Robustness")]
    [Tooltip("ระยะเวลาที่ IgnoreCollision ระหว่างผู้เล่น/วัตถุกับพอร์ทัลทั้งคู่")]
    [SerializeField] private float ignoreCollisionDuration = 0.45f;

    [Header("Optional Guard")]
    [Tooltip("กันเข้าจากด้านหน้าพอร์ทัล (ช่วยกันวาร์ปวน)")]
    [SerializeField] private bool requireFrontFaceEntry = false;
    [SerializeField, Range(-1f, 1f)] private float entryFaceDotThreshold = -0.2f;

    [Header("Net Scheduling")]
    [Tooltip("เวลาบัฟเฟอร์ก่อนเวลาจริงของเซิร์ฟเวอร์ เพื่อให้แพ็กเกจถึงทุกเครื่อง")]
    [SerializeField, Min(0.03f)] private float teleportLeadSeconds = 0.12f;

    private Portal_Net pair;
    private Collider myTrigger;
    private NetworkObject myNO;

    // คูลดาวน์
    private readonly Dictionary<ulong, float> cooldownUntilByClient = new();
    private readonly Dictionary<ulong, float> cooldownUntilByObject = new();

    // ---------- Scheduling (ฝั่ง client เก็บคิวไว้ ยิงพร้อมกัน) ----------
    private struct PendingTeleport
    {
        public ulong noId;
        public bool isRigidbody;
        public Vector3 pos;
        public Quaternion rot;
        public Vector3 vel;
        public Vector3 angVel;
        public bool zeroVel;
        public double executeServerTime;
    }
    private readonly List<PendingTeleport> _pending = new();

    // ---------- Unity ----------
    private void Awake()
    {
        myTrigger = GetComponent<Collider>();
        myTrigger.isTrigger = true;
        myNO = GetComponent<NetworkObject>();
    }
    
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        PlayPortalSfx(openSound);
    }
    
    public override void OnNetworkDespawn()
    {
        PlayPortalSfx(closeSound);
        base.OnNetworkDespawn();
    }
    
    private void OnDisable()
    {
        // fallback: ถ้าถูกปิดด้วย SetActive/disable component (ไม่ผ่าน despawn) ให้เล่น close
        if (IsSpawned) PlayPortalSfx(closeSound);
    }
    
    private void PlayPortalSfx(AudioClip clip)
    {
        if (clip == null) return;
        if (portalSfxVolume <= 0f) return;
        if (portalAudioSource == null) return;
        
        if (sfxMixerGroup != null) portalAudioSource.outputAudioMixerGroup = sfxMixerGroup;
        portalAudioSource.transform.position = transform.position;
        portalAudioSource.volume = Mathf.Clamp01(portalSfxVolume);
        portalAudioSource.pitch = 1f;
        portalAudioSource.PlayOneShot(clip);
    }

    private void Update()
    {
        if (!IsClient) return;
        if (_pending.Count == 0) return;

        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        double now = nm.ServerTime.Time;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            if (now + 1e-6 < p.executeServerTime) continue;

            if (nm.SpawnManager.SpawnedObjects.TryGetValue(p.noId, out var no))
            {
                if (p.isRigidbody)
                    ApplyRigidLocal(no, p.pos, p.rot, p.vel, p.angVel, p.zeroVel);
                else
                    ApplyPlayerLocal(no, p.pos, p.rot, p.zeroVel);
            }
            _pending.RemoveAt(i);
        }
    }

    // ---------- Pair/Config ----------
    public void SetIsA(bool value) => isA = value;
    public void SetPair(Portal_Net other) => pair = other;
    private Transform ExitXform => exitOffset ? exitOffset : transform;

    // ให้เซิร์ฟเวอร์ย้ายตัวพอร์ทัลเอง (ใช้จาก PairManager)
    public void TeleportSelf(Vector3 pos, Quaternion rot)
    {
        if (!IsServer) return;
        transform.SetPositionAndRotation(pos, rot);
    }

    // ---------- Trigger ----------
    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer || pair == null) return;

        // ----- 1) Boulder / วัตถุฟิสิกส์ (server-authority) -----
        if (TryGetBoulderRoot(other, out var boulderNO, out var rbBoulder))
        {
            if (requireFrontFaceEntry && !IsFrontFaceEntry(rbBoulder)) return;

            float now = Time.time;
            if (cooldownUntilByObject.TryGetValue(boulderNO.NetworkObjectId, out var untilObj) && now < untilObj)
                return;

            SetCooldownForObject(boulderNO.NetworkObjectId, now + reenterCooldown);
            pair.SetCooldownForObject(boulderNO.NetworkObjectId, now + reenterCooldown);

            var exit = pair.ExitXform;
            
            // คำนวณ offset จาก bounds ของวัตถุ เพื่อไม่ให้ collider ไปติดผนัง/หินด้านหลัง
            float boundsOffset = CalculateBoundsBasedOffset(boulderNO.gameObject, exit);
            float finalOffset = Mathf.Max(exitForwardPush, boundsOffset);
            Vector3 targetPos = exit.position + exit.forward * finalOffset + Vector3.up * exitUpPush;

            Quaternion entryRot = transform.rotation;
            Quaternion exitRot = pair.transform.rotation;
            Quaternion mappedRot = MapRotationByPortal(entryRot, exitRot, boulderNO.transform.rotation);

            Vector3 inVel  = rbBoulder.linearVelocity;
            Vector3 inAng  = rbBoulder.angularVelocity;
            Vector3 outVel = MapVectorByPortal(entryRot, exitRot, inVel);
            Vector3 outAng = MapVectorByPortal(entryRot, exitRot, inAng);

            // แจ้งสคริปต์หินให้ปรับแกนกลิ้งหลังวาร์ป
            var boulderScript = boulderNO.GetComponent<RollingBoulder>();
            if (boulderScript != null) boulderScript.UpdateRollDirection(outVel);

            // นัดเวลาเดียวกันทุกเครื่อง
            double execTime = NetworkManager.ServerTime.Time + teleportLeadSeconds;

            ScheduleTeleportClientRpc(
                boulderNO.NetworkObjectId, true,
                targetPos, mappedRot, outVel, outAng, zeroVelocityOnTeleport,
                execTime
            );

            StartCoroutine(ServerExecuteAt(execTime, () =>
            {
                ServerApplyTeleport_Rigidbody(boulderNO, rbBoulder, targetPos, mappedRot, outVel, outAng, zeroVelocityOnTeleport);
            }));

            StartCoroutine(ServerTempIgnoreObjectVsPortals(boulderNO, myTrigger, pair.myTrigger, ignoreCollisionDuration));
            return;
        }

        // ----- 2) ผู้เล่น (รองรับทั้ง CNT และ server-authority) -----
        if (!TryGetPlayerRoot(other, out var playerRootNO, out var rb, out var cc, out var ownerClientId))
            return;

        if (requireFrontFaceEntry && rb != null && !IsFrontFaceEntry(rb)) return;

        float now2 = Time.time;
        if (cooldownUntilByClient.TryGetValue(ownerClientId, out var until) && now2 < until)
            return;

        SetCooldownForClient(ownerClientId, now2 + reenterCooldown);
        pair.SetCooldownForClient(ownerClientId, now2 + reenterCooldown);

        var exitX = pair.ExitXform;
        Vector3 tPos = exitX.position + exitX.forward * exitForwardPush + Vector3.up * exitUpPush;

        // คำนวณ yaw ใหม่ให้ยืนตรง
        Quaternion entryPortalRot = transform.rotation;
        Quaternion exitPortalRot  = pair.transform.rotation;
        Quaternion playerRot      = playerRootNO.transform.rotation;

        Quaternion toLocal   = Quaternion.Inverse(entryPortalRot) * playerRot;
        Quaternion flipLocal = Quaternion.AngleAxis(180f, Vector3.up);
        Quaternion mapped    = exitPortalRot * (flipLocal * toLocal);

        Vector3 fwd = Vector3.ProjectOnPlane(mapped * Vector3.forward, Vector3.up);
        if (fwd.sqrMagnitude < 1e-4f)
        {
            fwd = Vector3.ProjectOnPlane(exitX.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f)
            {
                fwd = Vector3.ProjectOnPlane(exitX.right, Vector3.up);
                if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            }
        }
        Quaternion tRot = Quaternion.LookRotation(fwd.normalized, Vector3.up);

        // นัดเวลา
        double execTimePlayer = NetworkManager.ServerTime.Time + teleportLeadSeconds;

        bool hasCNT = playerRootNO.GetComponentInChildren<ClientNetworkTransform>() != null;

        if (hasCNT)
        {
            // เจ้าของย้ายตัวเองตรงเวลาเดียวกัน (client-authority)
            var toOwner = new ClientRpcParams {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { ownerClientId } }
            };
            OwnerPerformTeleportClientRpc(playerRootNO.NetworkObjectId, tPos, tRot, zeroVelocityOnTeleport, execTimePlayer, toOwner);

            // ผู้ชมคนอื่น ๆ snap ภาพตรงเวลาเดียวกัน (ลดความรู้สึกหน่วงสายตา)
            var others = BuildAllClientIdsExcept(ownerClientId);
            if (others != null && others.Count > 0)
            {
                var toOthers = new ClientRpcParams {
                    Send = new ClientRpcSendParams { TargetClientIds = others.ToArray() }
                };
                NonOwnerVisualSnapClientRpc(playerRootNO.NetworkObjectId, tPos, tRot, execTimePlayer, toOthers);
            }

            // เซิร์ฟเวอร์ไม่ย้าย player CNT เพื่อไม่ชน authority
        }
        else
        {
            // Server-authority player: นัดเวลา + เซิร์ฟเวอร์ย้ายจริง
            ScheduleTeleportClientRpc(
                playerRootNO.NetworkObjectId, false,
                tPos, tRot, Vector3.zero, Vector3.zero, zeroVelocityOnTeleport,
                execTimePlayer
            );

            StartCoroutine(ServerExecuteAt(execTimePlayer, () =>
            {
                ServerApplyTeleport_Player(playerRootNO, tPos, tRot, rb, cc);
            }));
        }

        // กันชนพอร์ทัลทันที (ระหว่างรอเวลา)
        StartCoroutine(ServerTempIgnorePlayerVsPortals(playerRootNO, myTrigger, pair.myTrigger, ignoreCollisionDuration));

        // เจ้าของก็ IgnoreCollision ฝั่ง client ด้วย
        var toOwnerOnly = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { ownerClientId } } };
        TempIgnoreOwnerCollisionsClientRpc(playerRootNO.NetworkObjectId, myNO.NetworkObjectId, pair.myNO.NetworkObjectId, ignoreCollisionDuration, toOwnerOnly);
    }

    // ---------- Cooldown helpers ----------
    public void SetCooldownForClient(ulong ownerClientId, float untilTime) => cooldownUntilByClient[ownerClientId] = untilTime;
    public void SetCooldownForObject(ulong objectId, float untilTime) => cooldownUntilByObject[objectId] = untilTime;

    // ---------- Mapping (portal plane flip) ----------
    private static Vector3 MapVectorByPortal(Quaternion entryPortalRot, Quaternion exitPortalRot, Vector3 inVector)
    {
        Vector3 local = Quaternion.Inverse(entryPortalRot) * inVector; // world -> entry local
        local.z = -local.z;                                             // flip ผ่านระนาบ (normal = local z)
        return exitPortalRot * local;                                   // -> exit world
    }

    private static Quaternion MapRotationByPortal(Quaternion entryPortalRot, Quaternion exitPortalRot, Quaternion inRotation)
    {
        Quaternion local = Quaternion.Inverse(entryPortalRot) * inRotation; // world -> entry local
        Quaternion flipLocal = Quaternion.AngleAxis(180f, Vector3.up);      // 180° รอบ up (local)
        return exitPortalRot * (flipLocal * local);                         // -> exit world
    }

    // ---------- Server apply ----------
    private void ServerApplyTeleport_Player(NetworkObject playerNO, Vector3 pos, Quaternion rot, Rigidbody rbIfAny, CharacterController ccIfAny)
    {
        if (ccIfAny != null)
        {
            ccIfAny.enabled = false;
            playerNO.transform.SetPositionAndRotation(pos, rot);
            ccIfAny.enabled = true;
            SnapNetworkTransform(playerNO, pos, rot);
        }
        else if (rbIfAny != null)
        {
            rbIfAny.position = pos;
            rbIfAny.rotation = rot;
            if (zeroVelocityOnTeleport)
            {
                rbIfAny.linearVelocity = Vector3.zero;
                rbIfAny.angularVelocity = Vector3.zero;
            }
            SnapNetworkTransform(playerNO, pos, rot);
        }
        else
        {
            playerNO.transform.SetPositionAndRotation(pos, rot);
            SnapNetworkTransform(playerNO, pos, rot);
        }
    }

    private void ServerApplyTeleport_Rigidbody(NetworkObject no, Rigidbody rb, Vector3 pos, Quaternion rot,
                                               Vector3 outVelocity, Vector3 outAngularVel, bool zeroVel)
    {
        rb.position = pos;
        rb.rotation = rot;

        if (zeroVel)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        else
        {
            rb.linearVelocity = outVelocity;
            rb.angularVelocity = outAngularVel;
        }
        SnapNetworkTransform(no, pos, rot);
    }

    // ---------- Schedule RPC (ทุกคนเก็บคิว แล้วกดย้ายพร้อมกัน) ----------
    [ClientRpc]
    private void ScheduleTeleportClientRpc(ulong noId, bool isRigidbody,
        Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVel, bool zeroVel,
        double executeServerTime)
    {
        _pending.Add(new PendingTeleport
        {
            noId = noId,
            isRigidbody = isRigidbody,
            pos = pos,
            rot = rot,
            vel = vel,
            angVel = angVel,
            zeroVel = zeroVel,
            executeServerTime = executeServerTime
        });
    }

    private IEnumerator ServerExecuteAt(double serverTimeToRun, System.Action action)
    {
        while (NetworkManager != null && NetworkManager.ServerTime.Time + 1e-6 < serverTimeToRun)
            yield return null;
        action?.Invoke();
    }

    // ---------- CNT: ให้เจ้าของย้ายเองตรงเวลา ----------
    [ClientRpc]
    private void OwnerPerformTeleportClientRpc(
        ulong playerNOId, Vector3 pos, Quaternion rot, bool zeroVel, double executeServerTime,
        ClientRpcParams rpcParams = default)
    {
        StartCoroutine(OwnerTeleportAtTime(playerNOId, pos, rot, zeroVel, executeServerTime));
    }

    private IEnumerator OwnerTeleportAtTime(
        ulong playerNOId, Vector3 pos, Quaternion rot, bool zeroVel, double executeServerTime)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) yield break;

        while (nm.ServerTime.Time + 1e-6 < executeServerTime)
            yield return null;

        if (!nm.SpawnManager.SpawnedObjects.TryGetValue(playerNOId, out var no)) yield break;
        if (!no.IsOwner) yield break; // ย้ายเฉพาะฝั่งเจ้าของ

        var cc = no.GetComponentInChildren<CharacterController>();
        var rb = no.GetComponentInChildren<Rigidbody>();

        if (cc != null)
        {
            cc.enabled = false;
            no.transform.SetPositionAndRotation(pos, rot);
            cc.enabled = true;
        }
        else if (rb != null)
        {
            rb.position = pos; rb.rotation = rot;
            if (zeroVel) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        }
        else
        {
            no.transform.SetPositionAndRotation(pos, rot);
        }
    }

    // ---------- CNT: ให้ non-owners snap ภาพตรงเวลา (ลดดิเลย์ที่ตาเห็น) ----------
    [ClientRpc]
    private void NonOwnerVisualSnapClientRpc(
        ulong playerNOId, Vector3 pos, Quaternion rot, double executeServerTime,
        ClientRpcParams rpcParams = default)
    {
        StartCoroutine(NonOwnerVisualSnapAtTime(playerNOId, pos, rot, executeServerTime));
    }

    private IEnumerator NonOwnerVisualSnapAtTime(ulong playerNOId, Vector3 pos, Quaternion rot, double executeServerTime)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) yield break;

        while (nm.ServerTime.Time + 1e-6 < executeServerTime)
            yield return null;

        if (!nm.SpawnManager.SpawnedObjects.TryGetValue(playerNOId, out var no)) yield break;
        if (no.IsOwner) yield break; // ห้ามชน authority ของเจ้าของ

        var cc = no.GetComponentInChildren<CharacterController>();
        if (cc != null)
        {
            cc.enabled = false;
            no.transform.SetPositionAndRotation(pos, rot);
            cc.enabled = true;
        }
        else
        {
            no.transform.SetPositionAndRotation(pos, rot);
        }
    }

    // ---------- IgnoreCollision ----------
    private IEnumerator ServerTempIgnorePlayerVsPortals(NetworkObject playerNO, Collider portalA, Collider portalB, float duration)
    {
        var cols = playerNO.GetComponentsInChildren<Collider>(true);
        foreach (var c in cols) { if (c) { Physics.IgnoreCollision(c, portalA, true); Physics.IgnoreCollision(c, portalB, true); } }
        yield return new WaitForSeconds(duration);
        foreach (var c in cols) { if (c) { Physics.IgnoreCollision(c, portalA, false); Physics.IgnoreCollision(c, portalB, false); } }
    }

    private IEnumerator ServerTempIgnoreObjectVsPortals(NetworkObject objNO, Collider portalA, Collider portalB, float duration)
    {
        var cols = objNO.GetComponentsInChildren<Collider>(true);
        foreach (var c in cols) { if (c) { Physics.IgnoreCollision(c, portalA, true); Physics.IgnoreCollision(c, portalB, true); } }
        yield return new WaitForSeconds(duration);
        foreach (var c in cols) { if (c) { Physics.IgnoreCollision(c, portalA, false); Physics.IgnoreCollision(c, portalB, false); } }
    }

    [ClientRpc]
    private void TempIgnoreOwnerCollisionsClientRpc(ulong playerNOId, ulong portalAId, ulong portalBId, float duration, ClientRpcParams rpcParams = default)
    {
        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(playerNOId, out var playerNO)) return;
        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(portalAId, out var portalANO)) return;
        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(portalBId, out var portalBNO)) return;

        var portalA = portalANO.GetComponent<Collider>();
        var portalB = portalBNO.GetComponent<Collider>();
        if (portalA == null || portalB == null) return;

        StartCoroutine(ClientTempIgnoreRoutine(playerNO, portalA, portalB, duration));
    }

    private IEnumerator ClientTempIgnoreRoutine(NetworkObject objNO, Collider portalA, Collider portalB, float duration)
    {
        var cols = objNO.GetComponentsInChildren<Collider>(true);
        foreach (var c in cols) { if (c) { Physics.IgnoreCollision(c, portalA, true); Physics.IgnoreCollision(c, portalB, true); } }
        yield return new WaitForSeconds(duration);
        foreach (var c in cols) { if (c) { Physics.IgnoreCollision(c, portalA, false); Physics.IgnoreCollision(c, portalB, false); } }
    }

    // ---------- Utilities ----------
    private bool TryGetBoulderRoot(Collider hitCol, out NetworkObject rootNO, out Rigidbody rb)
    {
        rootNO = null; rb = null;
        var boulder = hitCol.GetComponentInParent<RollingBoulder>();
        if (boulder == null) return false;

        rootNO = boulder.GetComponent<NetworkObject>();
        rb = boulder.GetComponent<Rigidbody>();
        return (rootNO != null && rb != null && rootNO.IsSpawned);
    }

    private bool TryGetPlayerRoot(Collider hitCol, out NetworkObject rootNO, out Rigidbody rb, out CharacterController cc, out ulong ownerClientId)
    {
        rootNO = null; rb = null; cc = null; ownerClientId = 0;

        var role = hitCol.GetComponentInParent<PlayerRoleFromLobby>();
        if (role != null)
        {
            rootNO = role.GetComponent<NetworkObject>();
            if (rootNO == null) return false;
        }
        else
        {
            var anyNO = hitCol.GetComponentInParent<NetworkObject>();
            if (anyNO == null) return false;
            rootNO = anyNO.transform.root.GetComponent<NetworkObject>();
            if (rootNO == null) rootNO = anyNO;
        }

        rb = rootNO.GetComponentInChildren<Rigidbody>();
        cc = rootNO.GetComponentInChildren<CharacterController>();
        if (rb == null && cc == null) return false;

        ownerClientId = rootNO.OwnerClientId;
        return true;
    }

    /// <summary>
    /// คำนวณ offset ที่ต้องเลื่อนวัตถุไปข้างหน้าจากจุด exit ของ portal
    /// โดยดูจาก Collider bounds ของวัตถุ เพื่อให้ collider ไม่ไปติดผนัง/หินด้านหลัง
    /// </summary>
    private float CalculateBoundsBasedOffset(GameObject obj, Transform exitTransform)
    {
        if (obj == null || exitTransform == null) return 0f;

        // รวม bounds จากทุก Collider
        var colliders = obj.GetComponentsInChildren<Collider>(true);
        if (colliders == null || colliders.Length == 0) return 0f;

        Bounds combinedBounds = new Bounds();
        bool first = true;

        foreach (var col in colliders)
        {
            if (col == null || col.isTrigger) continue;
            
            if (first)
            {
                combinedBounds = col.bounds;
                first = false;
            }
            else
            {
                combinedBounds.Encapsulate(col.bounds);
            }
        }

        if (first) return 0f; // ไม่มี non-trigger collider

        // คำนวณความลึกของ bounds ตามแนว forward ของ exit portal
        // ใช้แค่ครึ่งหนึ่งของ bounds เพื่อให้วัตถุเกิดใกล้พอร์ทัลมากขึ้น
        Vector3 fwd = exitTransform.forward;
        Vector3 extents = combinedBounds.extents;

        // ฉาย extents ลงบนแกน forward (หาความลึกตามทิศทางที่พอร์ทัลหัน)
        float depthOnForward = Mathf.Abs(extents.x * fwd.x) 
                             + Mathf.Abs(extents.y * fwd.y) 
                             + Mathf.Abs(extents.z * fwd.z);

        // เพิ่ม safety margin เล็กน้อย (0.2 เมตร)
        float safetyMargin = 0.2f;

        return depthOnForward + safetyMargin;
    }

    private bool IsFrontFaceEntry(Rigidbody rb)
    {
        if (rb == null) return true;
        // forward ของปากชี้ออกนอกพื้นผิว: การ "พุ่งเข้า" จะมี dot < 0
        return Vector3.Dot(rb.linearVelocity, transform.forward) < entryFaceDotThreshold;
    }

    private List<ulong> BuildAllClientIdsExcept(ulong excluded)
    {
        var nm = NetworkManager;
        if (nm == null) return null;
        var list = new List<ulong>();
        foreach (var id in nm.ConnectedClientsIds)
            if (id != excluded) list.Add(id);
        return list;
    }

    // ---------- Local apply + Snap (ไม่มี Teleport API) ----------
    private void ApplyRigidLocal(NetworkObject no, Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVel, bool zeroVel)
    {
        var rb = no.GetComponentInChildren<Rigidbody>();
        if (rb != null)
        {
            rb.position = pos;
            rb.rotation = rot;
            if (zeroVel) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            else { rb.linearVelocity = vel; rb.angularVelocity = angVel; }
        }

        no.transform.SetPositionAndRotation(pos, rot);
        NTSnapOneFrame(no); // ปิด interpolate 1 เฟรมให้สแน็ปเนียน
    }

    private void ApplyPlayerLocal(NetworkObject no, Vector3 pos, Quaternion rot, bool zeroVel)
    {
        var cc = no.GetComponentInChildren<CharacterController>();
        var rb = no.GetComponentInChildren<Rigidbody>();

        if (cc != null)
        {
            cc.enabled = false;
            no.transform.SetPositionAndRotation(pos, rot);
            cc.enabled = true;
        }
        else if (rb != null)
        {
            rb.position = pos; rb.rotation = rot;
            if (zeroVel) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        }

        no.transform.SetPositionAndRotation(pos, rot);
        NTSnapOneFrame(no);
    }

    private void SnapNetworkTransform(NetworkObject no, Vector3 pos, Quaternion rot)
    {
        // NGO 2.4.4 ไม่มี Teleport → เซ็ตทรานส์ฟอร์มตรง ๆ
        no.transform.SetPositionAndRotation(pos, rot);

        var cc = no.GetComponentInChildren<CharacterController>();
        if (cc != null)
        {
            cc.enabled = false;
            no.transform.SetPositionAndRotation(pos, rot);
            cc.enabled = true;
        }

        NTSnapOneFrame(no);
    }

    // ปิด Interpolate ของ NetworkTransform 1 เฟรมเพื่อให้ snap ทันที
    private void NTSnapOneFrame(NetworkObject no)
    {
        var nt = no.GetComponentInChildren<NetworkTransform>();
        if (nt == null) return;
        StartCoroutine(_SnapOneFrame(nt));
    }
    private IEnumerator _SnapOneFrame(NetworkTransform nt)
    {
        // บางเวอร์ชัน field อาจเป็น internal; ถ้าเซ็ตไม่ได้ ให้ปิด Interpolate ในอินสเปกเตอร์แทน
        bool prev = nt.Interpolate;
        nt.Interpolate = false;
        yield return null; // 1 เฟรม
        nt.Interpolate = prev;
    }
}
