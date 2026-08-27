using UnityEngine;
using System.Collections.Generic;

// สคริปต์นี้เป็นแค่ "ตู้เก็บข้อมูล" (Data Container)
// ไม่จำเป็นต้องเป็น NetworkBehaviour เพราะ Server จะเป็นคนอ่านค่านี้โดยตรง
public class SceneAbilityRule : MonoBehaviour
{
    [Header("Scene-Wide Rules")]
    [Tooltip("ID สกิลที่จะถูก 'เปิด' เสมอ ตลอดทั้งซีนนี้")]
    [SerializeField]
    private List<string> sceneWideEnableIDs;

    [Tooltip("ID สกิลที่จะถูก 'ปิด' เสมอ ตลอดทั้งซีนนี้")]
    [SerializeField]
    private List<string> sceneWideDisableIDs;

    // --- Public Getters ---
    // สร้างฟังก์ชันให้ PlayerAbilityManager (ฝั่ง Server) มาดึงค่าไปใช้
    
    public List<string> GetIDsToEnable()
    {
        return sceneWideEnableIDs;
    }

    public List<string> GetIDsToDisable()
    {
        return sceneWideDisableIDs;
    }

    private void Start()
    {
        // คำเตือน: ถ้ามีคนเผลอใส่ "ป้ายบอกกฎ" นี้ไว้หลายอันในซีน
        // มันจะทำงานแค่
        if (FindObjectsOfType<SceneAbilityRule>().Length > 1)
        {
            Debug.LogWarning($"[SceneAbilityRule] ตรวจพบ '{name}' มากกว่า 1 อันในซีนนี้ " +
                             "ระบบอาจทำงานไม่ถูกต้อง (จะอ่านค่าจากอันแรกที่เจอเท่านั้น)", this.gameObject);
        }
    }
}