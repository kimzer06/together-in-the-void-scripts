// ===== Shared Types =====
public enum WindMode
{
    Disabled = 0,
    Push = 1,   // ดันขึ้น/ตามทิศ push
    Pull = 2    // ดูดลง/ตามทิศ pull
}

public interface IActivatable
{
    void Activate(bool on);
}

public interface IWindModeActivatable
{
    void SetWindMode(WindMode mode);
}