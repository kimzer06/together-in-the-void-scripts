/// <summary>
/// Interface สำหรับ Manager ที่สามารถรับคำสั่ง "กดสวิตช์" ได้
/// (ทั้งแบบ Stepper และแบบ Toggle)
/// </summary>
public interface ISwitchableWindManager
{
    /// <summary>
    /// (SERVER-ONLY) ฟังก์ชันที่ SwitchWind จะเรียกเมื่อถูกกด
    /// </summary>
    void Server_OnSwitchPressed(float extraDelay);
}