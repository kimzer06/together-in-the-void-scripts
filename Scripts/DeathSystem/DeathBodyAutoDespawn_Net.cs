using UnityEngine;
using Unity.Netcode;

/// ติดกับพรีแฟบซากศพ (ที่มี NetworkObject)
/// - เซิร์ฟเวอร์จะเป็นคนจับเวลาและ Despawn ให้ทุกไคลเอนต์
[DisallowMultipleComponent]
public class DeathBodyAutoDespawn_Net : NetworkBehaviour
{
    [SerializeField] private float lifeTime = 10f;
    private float t;

    public void SetLifetime(float seconds) => lifeTime = Mathf.Max(0f, seconds);

    private void Update()
    {
        if (!IsServer) return; // นับเวลาที่เซิร์ฟเวอร์เท่านั้น
        if (lifeTime <= 0f) return;

        t += Time.unscaledDeltaTime;
        if (t >= lifeTime)
        {
            if (NetworkObject && NetworkObject.IsSpawned)
                NetworkObject.Despawn(true); // true = destroy gameobject บน host ด้วย
            else
                Destroy(gameObject);
        }
    }
}