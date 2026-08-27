using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Spawner แบบ Start→End + Object Pool (Server-authoritative)
/// มี "Model Forward Axis" ให้เลือกเพื่อชดเชยแกนของโมเดลให้ตรงเป๊ะ
/// </summary>
public class TrainPathSpawner : NetworkBehaviour
{
    // ระบุว่า "หน้า" ของโมเดลชี้แกนไหน (ใน local)
    public enum ModelForwardAxis { ZPlus, ZMinus, XPlus, XMinus, YPlus, YMinus }

    [Header("Setup")]
    [Tooltip("Prefab รถไฟที่มี NetworkObject (+ควรมี NetworkTransform) และลงทะเบียนใน NetworkManager/NetworkPrefabs แล้ว")]
    public NetworkObject trainPrefab;

    public Transform startPoint;
    public Transform endPoint;

    [Header("Pooling")]
    [Tooltip("จำนวนที่เตรียมไว้ล่วงหน้า")]
    public int prewarmCount = 5;

    private readonly Queue<NetworkObject> _pool = new Queue<NetworkObject>();

    [Header("Default Train Params")]
    [Tooltip("หน่วย/วินาที")]
    public float defaultSpeed = 20f;
    public float defaultArriveTolerance = 0.05f;
    public bool  defaultRotateToVelocity = true;

    [Header("Auto Spawn")]
    [Tooltip("สปอนอัตโนมัติทุกกี่วินาที (0 = ปิด)")]
    public float autoSpawnInterval = 0f;
    private float _nextAutoSpawnTime;

    [Header("Wait Until Arrived")]
    [Tooltip("เปิดโหมดไม่ spawn รถไฟตัวใหม่ จนกว่ารถไฟตัวปัจจุบันจะถึง End แล้ว")]
    public bool waitUntilArrived = false;
    private int _activeTrainCount = 0;

    [Header("Orientation (แนวหมุนให้ตรงเป๊ะ)")]
    [Tooltip("บอกว่า 'หน้าโมเดล' ชี้ไปทางแกนไหนของ local (ถ้าโมเดลคุณหันหน้าเป็น X+ ให้เลือก XPlus)")]
    public ModelForwardAxis modelForward = ModelForwardAxis.ZPlus;

    [Tooltip("ใช้แกน Up ของ startPoint เป็น up reference (เผื่อรางเอียง)")]
    public bool useStartUpAsUp = false;

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;
        if (!ValidateSetup()) return;

        Prewarm();

        // โหมด waitUntilArrived → spawn คันแรกทันที
        if (waitUntilArrived)
        {
            SpawnTrain(defaultSpeed, defaultArriveTolerance, defaultRotateToVelocity);
        }

        if (autoSpawnInterval > 0f)
            _nextAutoSpawnTime = Time.time + autoSpawnInterval;
    }

    private void Update()
    {
        if (!IsServer) return;
        if (autoSpawnInterval > 0f && Time.time >= _nextAutoSpawnTime)
        {
            if (waitUntilArrived && _activeTrainCount > 0) return;

            SpawnTrain(defaultSpeed, defaultArriveTolerance, defaultRotateToVelocity);
            _nextAutoSpawnTime = Time.time + autoSpawnInterval;
        }
    }

    private bool ValidateSetup()
    {
        if (trainPrefab == null)
        {
            Debug.LogError("[TrainPathSpawner] Missing trainPrefab.");
            return false;
        }
        if (startPoint == null || endPoint == null)
        {
            Debug.LogError("[TrainPathSpawner] Missing startPoint or endPoint.");
            return false;
        }
        return true;
    }

    private void Prewarm()
    {
        for (int i = 0; i < prewarmCount; i++)
        {
            var inst = Instantiate(trainPrefab);
            inst.gameObject.SetActive(false);
            _pool.Enqueue(inst);
        }
    }

    public void SpawnTrain(float speed, float arriveTolerance, bool rotateToVelocity)
    {
        if (!IsServer) return;
        if (!ValidateSetup()) return;
        if (waitUntilArrived && _activeTrainCount > 0) return;

        var netObj = GetFromPoolOrCreate();
        PrepareAndSpawn(netObj, speed, arriveTolerance, rotateToVelocity);
        _activeTrainCount++;

        if (waitUntilArrived)
            Debug.Log($"[TrainPathSpawner] Spawned train (activeCount={_activeTrainCount})");
    }

    [ServerRpc(RequireOwnership = false)]
    public void SpawnOnceServerRpc(float speed, float arriveTolerance, bool rotateToVelocity)
    {
        SpawnTrain(speed, arriveTolerance, rotateToVelocity);
    }

    [ServerRpc(RequireOwnership = false)]
    public void SpawnDefaultServerRpc()
    {
        SpawnTrain(defaultSpeed, defaultArriveTolerance, defaultRotateToVelocity);
    }

    private NetworkObject GetFromPoolOrCreate()
    {
        if (_pool.Count > 0) return _pool.Dequeue();
        var obj = Instantiate(trainPrefab);
        obj.gameObject.SetActive(false);
        return obj;
    }

    private void PrepareAndSpawn(NetworkObject netObj, float speed, float arriveTolerance, bool rotateToVelocity)
    {
        var go = netObj.gameObject;

        // 1) วางตำแหน่งที่ start
        go.transform.position = startPoint.position;

        // 2) คำนวณทิศทาง path + up อ้างอิง
        Vector3 pathDir = (endPoint.position - startPoint.position);
        if (pathDir.sqrMagnitude < 1e-10f) pathDir = startPoint.forward; // กันกรณีซ้อนจุด
        pathDir.Normalize();

        Vector3 upRef = useStartUpAsUp ? startPoint.up : Vector3.up;
        if (upRef.sqrMagnitude < 1e-10f) upRef = Vector3.up;

        // 3) หมุนตัวรถให้ 'หันตามทาง' (อิง upRef)
        Quaternion facePath = Quaternion.LookRotation(pathDir, upRef);

        // 4) คำนวณ "คอเร็กชัน" จากแกนหน้าโมเดล -> Z+ (เพื่อให้หน้าโมเดลชี้ไปตามทิศทาง path)
        Vector3 modelFwdVec = ModelAxisToVector(modelForward);
        // หมุนจาก 'แกนหน้าโมเดล' ให้มาเป็น Z+ ใน local
        Quaternion correction = Quaternion.FromToRotation(modelFwdVec, Vector3.forward);

        // 5) หมุนสุดท้าย: facePath * correction  (พิสูจน์แล้วว่าจะได้ 'หน้าโมเดล' = pathDir)
        Quaternion finalRot = facePath * correction;

        go.transform.rotation = finalRot;

        // 6) ใส่/ตั้งค่า mover
        var mover = go.GetComponent<TrainPathMover>();
        if (mover == null) mover = go.AddComponent<TrainPathMover>();
        mover.startPoint = startPoint;
        mover.endPoint = endPoint;
        mover.speed = speed;
        mover.arriveTolerance = arriveTolerance;
        mover.rotateToVelocity = rotateToVelocity;
        mover.Spawner = this;

        // 7) เปิดใช้งานก่อน Spawn (สำหรับ Netcode)
        go.SetActive(true);

        if (!netObj.IsSpawned)
            netObj.Spawn(true);

        // Debug ที่เป็นประโยชน์
        Debug.Log($"[TrainPathSpawner] Spawned at {startPoint.position} -> {endPoint.position} | modelForward={modelForward} | speed={speed}");
    }

    /// <summary>
    /// เรียกโดย TrainPathMover เมื่อรถไฟถึง End แล้ว
    /// </summary>
    public void NotifyTrainArrived()
    {
        _activeTrainCount = Mathf.Max(0, _activeTrainCount - 1);

        // ถ้าเปิดโหมด waitUntilArrived และไม่มีรถเหลืออยู่ → spawn คันต่อไปทันที
        if (waitUntilArrived && _activeTrainCount == 0)
        {
            SpawnTrain(defaultSpeed, defaultArriveTolerance, defaultRotateToVelocity);
        }
    }

    public void ReturnToPool(NetworkObject netObj)
    {
        if (netObj == null) return;
        if (netObj.IsSpawned) netObj.Despawn(false);
        _pool.Enqueue(netObj);
    }

    private static Vector3 ModelAxisToVector(ModelForwardAxis axis)
    {
        switch (axis)
        {
            case ModelForwardAxis.ZPlus:  return Vector3.forward;
            case ModelForwardAxis.ZMinus: return Vector3.back;
            case ModelForwardAxis.XPlus:  return Vector3.right;
            case ModelForwardAxis.XMinus: return Vector3.left;
            case ModelForwardAxis.YPlus:  return Vector3.up;
            case ModelForwardAxis.YMinus: return Vector3.down;
            default: return Vector3.forward;
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.green;
        if (startPoint) Gizmos.DrawCube(startPoint.position, Vector3.one * 0.25f);
        if (endPoint)   Gizmos.DrawCube(endPoint.position,   Vector3.one * 0.25f);
        if (startPoint && endPoint)
        {
            Gizmos.DrawLine(startPoint.position, endPoint.position);
            // แสดงลูกศรทิศทางเล็กน้อย
            Vector3 dir = (endPoint.position - startPoint.position);
            if (dir.sqrMagnitude > 1e-6f)
            {
                dir.Normalize();
                Vector3 head = startPoint.position + dir * Mathf.Min(2f, Vector3.Distance(startPoint.position, endPoint.position));
                Gizmos.DrawWireSphere(head, 0.12f);
            }
        }
    }
#endif
}
