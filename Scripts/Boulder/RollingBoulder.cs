using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine.Audio;

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(NetworkTransform))] // Server authority แนะนำ
[DisallowMultipleComponent]
public class RollingBoulder : NetworkBehaviour, IFreezeListener
{
    public enum Axis { X, Y, Z }
    public enum Direction { Positive, Negative }

    [Header("Roll Settings")]
    [SerializeField] public Axis axis = Axis.X;
    [SerializeField] public Direction direction = Direction.Positive;

    [Header("Boulder Type")]
    [Tooltip("ประเภทของหินนี้ (เช่น Red, Black, Blue) - ใช้สำหรับ Puzzle ที่ต้องการเช็คประเภทหิน")]
    [SerializeField] private string boulderType = "";

    public string BoulderType => boulderType;

    [Header("Forces")]
    [SerializeField, Min(0f)] private float initialImpulse = 10f;
    [SerializeField, Min(0f)] private float torque = 20f;
    [SerializeField, Min(0f)] private float maxSpeed = 18f;
    [Tooltip("แรงกดเพิ่ม ให้เกาะพื้นดีขึ้น")]
    [SerializeField, Min(0f)] private float extraGravity = 0f;

    [Header("Effects")]
    [Tooltip("Particle prefab ที่จะถูกสร้างและเล่นเมื่อหินถูกทำลาย")]
    [SerializeField] private ParticleSystem destroyParticlePrefab;
    [Tooltip("เสียงที่จะเล่นเมื่อหินถูกทำลาย")]
    [SerializeField] private AudioClip destroySound;
    [Tooltip("เสียงที่จะเล่นตอนเริ่มกลิ้ง (ตอน Spawn/เริ่มเคลื่อนที่)")]
    [SerializeField] private AudioClip rollStartSound;
    [Tooltip("Audio Source สำหรับตำแหน่งเกิดเสียง (กำหนดใน Inspector)")]
    [SerializeField] private AudioSource audioSource;
    [Tooltip("Audio Mixer Group สำหรับส่ง Output (ถ้าต้องการ)")]
    [SerializeField] private AudioMixerGroup outputMixerGroup;

    [HideInInspector] public bool returnToPool = false; // Spawner ตั้งค่า

    // ----- Runtime -----
    private Rigidbody rb;
    private bool _despawnedNotified = false;
    private bool _isFrozen;
    private bool _isRollStartPlaying;
    private bool _rollStartPending;

    // อีเวนต์ฝั่งเซิร์ฟเวอร์: แจ้ง Spawner เมื่อ Despawn แล้ว (คืนพูล/นับจำนวน)
    public event System.Action<RollingBoulder> ServerDespawned;

    public void Configure(Axis ax, Direction dir) { axis = ax; direction = dir; }

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.maxAngularVelocity = 100f;
        InitializeAudio();
    }

    private void InitializeAudio()
    {
        if (audioSource == null) return;
        audioSource.playOnAwake = false;
        audioSource.loop = false;
        if (outputMixerGroup != null)
        {
            audioSource.outputAudioMixerGroup = outputMixerGroup;
        }
    }

    public void OnFreezeChanged(bool on)
    {
        _isFrozen = on;

        if (audioSource == null) return;

        // Freeze: pause ถ้ากำลังเล่น rollStart อยู่
        if (_isFrozen)
        {
            if (_isRollStartPlaying && audioSource.isPlaying && audioSource.clip == rollStartSound)
            {
                audioSource.Pause();
            }
            return;
        }

        // Unfreeze: ถ้าเคย pause ไว้ ให้เล่นต่อ
        if (_isRollStartPlaying && audioSource.clip == rollStartSound)
        {
            audioSource.UnPause();
            return;
        }

        // Unfreeze: ถ้ามีคิวรอ (โดน freeze ตอนเริ่ม) ให้เริ่มเล่นทันที
        if (_rollStartPending && rollStartSound != null)
        {
            _rollStartPending = false;
            audioSource.loop = false;
            audioSource.clip = rollStartSound;
            audioSource.Play();
            _isRollStartPlaying = true;
        }
    }

    public override void OnNetworkSpawn()
    {
        // รีเซ็ต Collider ทุกครั้งที่ spawn (กัน pool reuse ค้างปิดจาก BoulderTrapZone)
        foreach (var col in GetComponentsInChildren<Collider>())
            col.enabled = true;

        if (!IsServer) return;

        _despawnedNotified = false;
        _pendingBoulderCollisionKill = false;

        // รีเซ็ตความเร็ว (กรณีเกิดจากพูล)
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        // คำนวณทิศเริ่มจาก axis/direction
        Vector3 dir = axis == Axis.X ? Vector3.right :
                      axis == Axis.Y ? Vector3.up    :
                                        Vector3.forward;
        if (direction == Direction.Negative) dir = -dir;

        // แรงดันเริ่ม + ทอร์คเริ่มให้ดู "กลิ้งจริง"
        if (initialImpulse > 0f) rb.AddForce(dir * initialImpulse, ForceMode.Impulse);

        Vector3 spinAxis = Vector3.Cross(Vector3.up, dir);
        if (spinAxis.sqrMagnitude < 1e-6f) spinAxis = Vector3.right; // กันกรณีขนาน
        if (torque > 0f) rb.AddTorque(spinAxis.normalized * torque, ForceMode.Impulse);

        // เล่นเสียงเริ่มกลิ้งให้ทุก client ได้ยินพร้อมกัน
        PlayRollStartSoundClientRpc(transform.position);
    }

    [ClientRpc]
    private void PlayRollStartSoundClientRpc(Vector3 position)
    {
        if (rollStartSound == null) return;

        if (audioSource != null)
        {
            // ใช้ clip/play เพื่อให้ Pause/UnPause ได้ตอน Freeze
            if (_isFrozen)
            {
                _rollStartPending = true;
                return;
            }
            audioSource.loop = false;
            audioSource.clip = rollStartSound;
            audioSource.Play();
            _isRollStartPlaying = true;
            return;
        }

        AudioSource.PlayClipAtPoint(rollStartSound, position);
    }

    void FixedUpdate()
    {
        if (!IsServer) return;

        if (extraGravity > 0f)
            rb.AddForce(Physics.gravity.normalized * extraGravity, ForceMode.Acceleration);

        float v = rb.linearVelocity.magnitude;
        if (v > maxSpeed && v > 0.0001f)
            rb.linearVelocity = rb.linearVelocity.normalized * maxSpeed;
    }

    /// <summary>
    /// เรียกโดย Portal/ระบบอื่น หลังเทเลพอร์ต เพื่อบังคับให้หินออกตามทิศใหม่ (World Space)
    /// - รีเซ็ตความเร็ว/โมเมนตัมเดิม
    /// - ใส่แรงเริ่มต้น+ทอร์คใหม่ตามทิศที่กำหนด
    /// - อัปเดต axis/direction ภายในเพื่อให้ state ตรงกับทิศใหม่
    /// </summary>
    public void UpdateRollDirection(Vector3 newWorldDirection)
    {
        if (!IsServer) return;

        // ปรับให้เป็นเวกเตอร์หน่วย
        if (newWorldDirection.sqrMagnitude < 1e-8f) return;
        newWorldDirection.Normalize();

        // ตัดสินแกนภายใน (X หรือ Z) จากทิศใหม่ เพื่อเก็บ state ให้สอดคล้อง
        float dotX = Vector3.Dot(newWorldDirection, Vector3.right);
        float dotZ = Vector3.Dot(newWorldDirection, Vector3.forward);

        if (Mathf.Abs(dotX) > Mathf.Abs(dotZ))
        {
            axis = Axis.X;
            direction = (dotX >= 0f) ? Direction.Positive : Direction.Negative;
        }
        else
        {
            axis = Axis.Z;
            direction = (dotZ >= 0f) ? Direction.Positive : Direction.Negative;
        }

        // รีเซ็ตโมเมนตัมเก่า แล้วอัดแรง/ทอร์คใหม่ตามทิศ
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        if (initialImpulse > 0f)
            rb.AddForce(newWorldDirection * initialImpulse, ForceMode.Impulse);

        Vector3 spinAxis = Vector3.Cross(Vector3.up, newWorldDirection);
        if (spinAxis.sqrMagnitude < 1e-6f) spinAxis = Vector3.right;
        if (torque > 0f)
            rb.AddTorque(spinAxis.normalized * torque, ForceMode.Impulse);
    }

    // ===== Boulder-to-Boulder Collision =====

    /// <summary>
    /// ป้องกันการ Kill ซ้ำจากการชนกันของ boulder 2 ตัว
    /// (ทั้ง 2 ตัวจะได้รับ OnCollisionEnter พร้อมกัน)
    /// </summary>
    private bool _pendingBoulderCollisionKill = false;

    private void OnCollisionEnter(Collision collision)
    {
        if (!IsServer) return;
        if (_pendingBoulderCollisionKill) return;

        var otherBoulder = collision.collider.GetComponentInParent<RollingBoulder>();
        if (otherBoulder == null) return;
        if (otherBoulder == this) return;

        // ทำเครื่องหมายทั้ง 2 ตัวเพื่อกันการประมวลผลซ้ำ
        _pendingBoulderCollisionKill = true;
        otherBoulder._pendingBoulderCollisionKill = true;

        // ทำลายทั้งคู่พร้อม particle + sound
        otherBoulder.KillImmediateServer(playFx: true);
        KillImmediateServer(playFx: true);
    }

    // ===== Kill / Despawn =====

    /// <summary>สั่งฆ่า (คืนพูล/Despawn) ทันที — เรียกฝั่งเซิร์ฟเวอร์เท่านั้น</summary>
    public void KillImmediateServer(bool playFx = true)
    {
        if (!IsServer) return;
        SafeDespawn(playFx);
    }

    /// <summary>สำหรับคลไคลเอนต์เรียกขอ — จะถูกทำบนเซิร์ฟเวอร์</summary>
    [ServerRpc(RequireOwnership = false)]
    public void KillServerRpc(bool playFx = true)
    {
        SafeDespawn(playFx);
    }

    private void SafeDespawn(bool playFx)
    {
        if (_despawnedNotified) return;
        if (NetworkObject && NetworkObject.IsSpawned)
        {
            if (playFx)
            {
                // เล่น Particle/Sound บนทุก Client ก่อน Despawn (FX ถูกสร้างแยกออกมา ไม่ตามหิน)
                PlayDestroyFxClientRpc(transform.position, transform.rotation);
            }

            // ถ้าใช้พูล: false = ไม่ทำลาย GameObject เพื่อให้ Spawner เก็บคืนคิว
            NetworkObject.Despawn(!returnToPool);
            // OnNetworkDespawn จะตามมา
        }
    }

    [ClientRpc]
    private void PlayDestroyFxClientRpc(Vector3 position, Quaternion rotation)
    {
        PlayDestroySound(position);

        if (destroyParticlePrefab == null) return;

        var ps = Instantiate(destroyParticlePrefab, position, rotation);
        ps.Play();
        Destroy(ps.gameObject, ps.main.duration + ps.main.startLifetime.constantMax);
    }

    private void PlayDestroySound(Vector3 position)
    {
        if (destroySound == null) return;

        // สำคัญ: หินจะถูก Despawn ทันทีหลัง ClientRpc
        // ถ้าเล่นผ่าน audioSource ที่อยู่บนหิน เสียงอาจถูกตัดเมื่อ object หายไป
        // ดังนั้นให้สร้าง one-shot audio แยกออกมาเสมอ
        var go = new GameObject("OneShotAudio_DestroySound");
        go.transform.position = position;

        var src = go.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.clip = destroySound;

        if (audioSource != null)
        {
            // copy setting หลัก ๆ จาก source เดิม เพื่อให้ได้ระยะ/3D feel ที่ตั้งไว้
            src.outputAudioMixerGroup = audioSource.outputAudioMixerGroup;
            src.spatialBlend = audioSource.spatialBlend;
            src.rolloffMode = audioSource.rolloffMode;
            src.minDistance = audioSource.minDistance;
            src.maxDistance = audioSource.maxDistance;
            src.dopplerLevel = audioSource.dopplerLevel;
            src.spread = audioSource.spread;
            src.priority = audioSource.priority;
            src.volume = audioSource.volume;
            src.pitch = audioSource.pitch;
        }
        else
        {
            // default เป็นเสียง 3D
            src.spatialBlend = 1f;
        }

        src.Play();
        Destroy(go, destroySound.length + 0.1f);
    }

    public override void OnNetworkDespawn()
    {
        if (!IsServer) return;
        if (_despawnedNotified) return;
        _despawnedNotified = true;

        ServerDespawned?.Invoke(this);
        ServerDespawned = null;
    }
}
