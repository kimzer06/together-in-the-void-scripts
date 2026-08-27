using Unity.Netcode;
using UnityEngine;

public class VisualSmoother : NetworkBehaviour
{
    [SerializeField] Transform visual;    // drag child "Visual" มาวาง
    [SerializeField] float posLerp = 15f; // ค่ายิ่งสูงยิ่งตามไว
    [SerializeField] float rotLerp = 15f;

    void LateUpdate()
    {
        if (!IsSpawned || IsOwner || visual == null) return;

        float t1 = 1f - Mathf.Exp(-posLerp * Time.deltaTime);
        float t2 = 1f - Mathf.Exp(-rotLerp * Time.deltaTime);

        visual.position = Vector3.Lerp(visual.position, transform.position, t1);
        visual.rotation = Quaternion.Slerp(visual.rotation, transform.rotation, t2);
    }
}
