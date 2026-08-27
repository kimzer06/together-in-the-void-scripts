using UnityEngine;
using Unity.Netcode;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Collections;

public class LevelExitTrigger : NetworkBehaviour
{
    [Header("Next Scene Settings")]
    [SerializeField] private string nextSceneName = "Level02";
    [SerializeField] private float fadeOutDuration = 0.8f;
    [SerializeField] private float extraHoldBeforeLoad = 0.2f; // กันกรณีเครื่องช้า

    private readonly HashSet<ulong> _clientsInside = new();
    private bool _busy;

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer) return;

        var no = other.GetComponentInParent<NetworkObject>();
        if (no == null) return;

        // นับเฉพาะ PlayerObject ของแต่ละ Client
        if (no.IsPlayerObject)
        {
            // ป้องกันกรณี host เดินชนด้วย object อื่น
            _clientsInside.Add(no.OwnerClientId);

            TryTransit();
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (!IsServer) return;

        var no = other.GetComponentInParent<NetworkObject>();
        if (no == null) return;

        if (no.IsPlayerObject)
        {
            _clientsInside.Remove(no.OwnerClientId);
        }
    }

    private void TryTransit()
    {
        if (_busy) return;
        // ต้อง “สอง client” เต็มจำนวน (กรณีคุณตั้ง max 2)
        if (_clientsInside.Count >= 2)
        {
            _busy = true;
            // 1) บอกทุกเครื่องให้เฟดดำ
            FadeOutAllClientsClientRpc(fadeOutDuration);

            // 2) Host หน่วงตามระยะเฟด แล้วค่อย Load ซีนใหม่
            StartCoroutine(LoadNextSceneAfterDelay(fadeOutDuration + extraHoldBeforeLoad));
        }
    }

    IEnumerator LoadNextSceneAfterDelay(float delay)
    {
        yield return new WaitForSecondsRealtime(delay);

        // โหลดซีนผ่าน LoadingScreenManager แทนการโหลดบรรทัดเดี่ยวเพื่อไปพักหน้าจอ Loading ก่อน
        LoadingScreenManager.LoadSceneNetworked(nextSceneName);
        // ไม่ต้องส่ง FadeIn ตอนนี้ เพราะวัตถุนี้จะถูก destroy ไป; ค่อยไปทำ FadeIn ด้วยสคริปต์ CoopSceneStarter ในฉากใหม่แทน
    }

    [ClientRpc]
    private void FadeOutAllClientsClientRpc(float duration)
    {
        if (ScreenFader.I != null)
            ScreenFader.I.FadeOut(duration);
    }
}
