using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

[RequireComponent(typeof(Selectable))]
public class UIHoverArrowEffect : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, ISelectHandler, IDeselectHandler
{
    [Header("Arrow Objects")]
    [Tooltip("ลาก GameObject ลูกศรซ้ายมาใส่ช่องนี้ (Arrow L)")]
    public GameObject leftArrow;
    [Tooltip("ลาก GameObject ลูกศรขวามาใส่ช่องนี้ (Arrow R)")]
    public GameObject rightArrow;

    [Header("Custom Color Tint (Optional)")]
    [Tooltip("ใส่ Image หรือ Text ที่ต้องการเปลี่ยนสีเมื่อเมาส์ชี้ (ถ้าใช้ Color Tint ของ Button อยู่แล้ว ไม่ต้องใส่ก็ได้)")]
    public Graphic targetGraphic;
    public Color normalColor = Color.white;
    public Color hoverColor = new Color(0.7f, 0.7f, 0.7f, 1f); // สีเข้มเทาๆ

    private void Start()
    {
        // เริ่มต้นให้ซ่อนลูกศรเมื่อยังไม่ได้เอาเมาส์ไปวาง
        SetHoverState(false);
    }

    // เมื่อเมาส์มาวางทับปุ่ม
    public void OnPointerEnter(PointerEventData eventData)
    {
        SetHoverState(true);
    }

    // เมื่อเอาเมาส์ออกจากปุ่ม
    public void OnPointerExit(PointerEventData eventData)
    {
        SetHoverState(false);
    }

    // เมื่อปุ่มถูกเลือกด้วย Keyboard / Controller
    public void OnSelect(BaseEventData eventData)
    {
        SetHoverState(true);
    }

    // เมื่อปุ่มถูกยกเลิกการเลือก
    public void OnDeselect(BaseEventData eventData)
    {
        SetHoverState(false);
    }

    // เมื่อปุ่ม/แพนลถูกซ่อน (SetActive(false)) จะรีเซ็ตสถานะ hover ทันที
    private void OnDisable()
    {
        SetHoverState(false);
    }

    private void SetHoverState(bool isHovered)
    {
        // เปิด-ปิด ลูกศรตามสถานะ
        if (leftArrow != null) leftArrow.SetActive(isHovered);
        if (rightArrow != null) rightArrow.SetActive(isHovered);

        // เปลี่ยนสีถ้ามีการกำหนด targetGraphic ไว้
        if (targetGraphic != null)
        {
            targetGraphic.color = isHovered ? hoverColor : normalColor;
        }
    }
}
