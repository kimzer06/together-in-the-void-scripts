using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

public class LanguageDropdownController : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private TMP_Dropdown languageDropdown;

    // ตั้งค่าจาก Inspector
    public enum DropdownOrder { ThaiFirst, EnglishFirst }
    public enum InitialLanguage { Thai, English, System } // System = ตาม SystemLanguage (ถ้าไม่ใช่ th/en จะ fallback)
    
    [Header("Behavior")]
    [Tooltip("ลำดับภาษาใน Dropdown")]
    public DropdownOrder dropdownOrder = DropdownOrder.ThaiFirst;

    [Tooltip("ถ้าเคยเลือกภาษาไว้แล้วจะใช้ค่าที่เซฟ (PlayerPrefs)")]
    public bool respectSavedChoice = true;

    [Tooltip("ภาษาเริ่มต้นครั้งแรก (เมื่อยังไม่เคยเซฟไว้ หรือถ้าปิด respectSavedChoice)")]
    public InitialLanguage startupLanguage = InitialLanguage.Thai;

    // ----- ภายใน -----
    private string[] localeCodes;     // จะถูกเซ็ตตาม dropdownOrder
    private string[] displayNames;    // จะถูกเซ็ตตาม dropdownOrder
    private const string PlayerPrefsKey = "lang"; // เก็บรหัสภาษา เช่น "th-TH" หรือ "en"

    private void Reset()
    {
        if (languageDropdown == null) languageDropdown = GetComponent<TMP_Dropdown>();
    }

    private void Awake()
    {
        if (languageDropdown == null) languageDropdown = GetComponent<TMP_Dropdown>();
        BuildArraysByOrder();
    }

    private void Start()
    {
        SetupDropdown();
        StartCoroutine(ApplyInitialLanguage());
    }

    private void OnDestroy()
    {
        if (languageDropdown != null)
            languageDropdown.onValueChanged.RemoveListener(OnLanguageChanged);
    }

    // จัดเรียง arrays ให้ตรงกับตัวเลือก dropdownOrder
    private void BuildArraysByOrder()
    {
        if (dropdownOrder == DropdownOrder.ThaiFirst)
        {
            // ใช้รหัสตรงกับโปรเจกต์จริง ๆ: ไทย (Thailand) = th-TH, อังกฤษ = en (หรือ en-US ก็หาแบบกว้างให้)
            localeCodes  = new[] { "th-TH", "en" };
            displayNames = new[] { "ไทย", "English" };
        }
        else
        {
            localeCodes  = new[] { "en", "th-TH" };
            displayNames = new[] { "English", "ไทย" };
        }
    }

    private void SetupDropdown()
    {
        languageDropdown.ClearOptions();
        var options = new List<TMP_Dropdown.OptionData>();
        foreach (var name in displayNames)
            options.Add(new TMP_Dropdown.OptionData(name));

        languageDropdown.AddOptions(options);
        languageDropdown.onValueChanged.AddListener(OnLanguageChanged);
    }

    private IEnumerator ApplyInitialLanguage()
    {
        // รอระบบ Localization พร้อม
        yield return LocalizationSettings.InitializationOperation;

        string codeToUse = null;

        // 1) ถ้าเลือกให้เคารพค่าที่เคยเซฟ และมีค่า → ใช้ค่านั้น
        if (respectSavedChoice)
        {
            var saved = PlayerPrefs.GetString(PlayerPrefsKey, "");
            if (!string.IsNullOrEmpty(saved))
                codeToUse = saved;
        }

        // 2) ถ้ายังไม่มี ให้ใช้ตาม InitialLanguage ที่กำหนด
        if (string.IsNullOrEmpty(codeToUse))
        {
            switch (startupLanguage)
            {
                case InitialLanguage.Thai:
                    codeToUse = "th-TH";
                    break;
                case InitialLanguage.English:
                    codeToUse = "en";
                    break;
                case InitialLanguage.System:
                    // map จาก SystemLanguage แบบคร่าว ๆ
                    codeToUse = (Application.systemLanguage == SystemLanguage.Thai) ? "th-TH" :
                                (Application.systemLanguage == SystemLanguage.English) ? "en" : "en";
                    break;
            }
        }

        // ตั้ง dropdown ให้ตรงกับ code ที่เลือก
        int index = IndexOfCode(codeToUse);
        if (index < 0) index = 0; // กันพลาด

        languageDropdown.SetValueWithoutNotify(index);
        yield return SetLanguage(localeCodes[index]);
    }

    private void OnLanguageChanged(int index)
    {
        if (index < 0 || index >= localeCodes.Length) return;
        StartCoroutine(SetLanguage(localeCodes[index]));
    }

    private IEnumerator SetLanguage(string code)
    {
        yield return LocalizationSettings.InitializationOperation;

        // หา exact ก่อน
        Locale locale = LocalizationSettings.AvailableLocales.GetLocale(code);

        // ถ้าไม่เจอ ลองหาแบบภาษา (กว้าง) เช่น "en-US" จะ match กับ "en"
        if (locale == null)
        {
            if (code.StartsWith("th"))
                locale = LocalizationSettings.AvailableLocales.GetLocale(SystemLanguage.Thai);
            else if (code.StartsWith("en"))
                locale = LocalizationSettings.AvailableLocales.GetLocale(SystemLanguage.English);
        }

        if (locale != null)
        {
            LocalizationSettings.SelectedLocale = locale;
            PlayerPrefs.SetString(PlayerPrefsKey, code);
            PlayerPrefs.Save();
        }
        else
        {
            Debug.LogWarning($"Locale '{code}' not found. ตรวจใน Project Settings → Localization → Locales");
        }
    }

    private int IndexOfCode(string code)
    {
        for (int i = 0; i < localeCodes.Length; i++)
            if (localeCodes[i].Equals(code, System.StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
