using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// แก้คำแปลใน <see cref="TranslationCsv"/> จากใน Unity — <c>Tools/Together/Localization/Translation Editor</c>
///
/// - <b>ตรวจ Dialogue</b>: ไล่ DialogueSequence ทุกไฟล์ → บรรทัดที่ยังไม่มีใน CSV เพิ่มเป็นแถวใหม่ (คำแปลว่าง)
///   + แถว Dialogue ที่ไม่มีบทไหนใช้แล้ว (แก้/ลบประโยคอังกฤษไปแล้ว) ขึ้นป้าย "ไม่มีบทนี้แล้ว" — คัดลอกคำแปลเก่าไปใช้ต่อแล้วลบทิ้งได้
/// - แก้ช่องไทย → Save (เขียน CSV) / Save + Import (เขียน + ส่งเข้า String Table ทันที) · Ctrl+Z ได้ · ปิดหน้าต่างตอนยังไม่เซฟจะถาม
/// - ช่อง key (อังกฤษ) อ่านอย่างเดียว — ต้องตรงกับต้นฉบับ แก้ต้นฉบับที่ DialogueSequence / ซีนแล้วกดตรวจใหม่
/// </summary>
public class TranslationEditorWindow : EditorWindow
{
    private enum Filter { All, Untranslated, Problems }

    private struct DialogueUse
    {
        public DialogueSequence sequence;
        public int index;
        public DialogueSpeaker speaker;
    }

    private static readonly string[] TableOptions = { "All", L10n.UiTable, L10n.DialogueTable };
    private static readonly string[] FilterLabels = { "ทั้งหมด", "ยังไม่แปล", "มีปัญหา" };

    [SerializeField] private List<TranslationCsv.Row> rows = new List<TranslationCsv.Row>();
    [SerializeField] private bool dirty;
    [SerializeField] private string search = "";
    [SerializeField] private int tableIndex;
    [SerializeField] private Filter filter;
    [SerializeField] private long loadedFileTime;

    private Dictionary<string, List<DialogueUse>> _uses;   // key → บทที่ใช้ประโยคนี้
    private HashSet<string> _duplicates;
    private Vector2 _scroll;
    private GUIStyle _keyStyle, _thStyle, _badgeStyle;

    [MenuItem("Tools/Together/Localization/Translation Editor", priority = 10)]
    public static TranslationEditorWindow Open()
    {
        var w = GetWindow<TranslationEditorWindow>();
        w.titleContent = new GUIContent("Translations (TH)");
        w.minSize = new Vector2(520, 300);
        return w;
    }

    [MenuItem("Tools/Together/Localization/Check Dialogue Translations", priority = 11)]
    public static void CheckDialogueMenu() => Open().CheckDialogue();

    private void OnEnable()
    {
        saveChangesMessage = "คำแปลยังไม่ได้เซฟลง CSV — เซฟก่อนปิดไหม?";
        hasUnsavedChanges = dirty;
        if (rows.Count == 0 && !dirty) Load();
        RefreshUses();
        Undo.undoRedoPerformed += OnUndoRedo;
    }

    private void OnDisable() => Undo.undoRedoPerformed -= OnUndoRedo;

    private void OnUndoRedo()
    {
        hasUnsavedChanges = dirty;
        RefreshDuplicates();
        Repaint();
    }

    private void OnFocus()
    {
        // แก้ CSV จากโปรแกรมอื่นมา และหน้านี้ไม่มีของค้าง → โหลดใหม่ให้เอง
        if (!dirty && TranslationCsv.Exists && FileTime() != loadedFileTime) Load();
        RefreshUses();
    }

    // ───────── data ─────────

    private void Load()
    {
        rows = TranslationCsv.Load();
        loadedFileTime = FileTime();
        SetDirty(false);
        RefreshDuplicates();
    }

    private void Save(bool import)
    {
        TranslationCsv.Save(rows);
        AssetDatabase.ImportAsset(TranslationCsv.Path);
        loadedFileTime = FileTime();
        SetDirty(false);
        if (import) TranslationImporter.Import();
        ShowNotification(new GUIContent(import ? "เซฟ + Import แล้ว" : "เซฟแล้ว"));
    }

    public override void SaveChanges()
    {
        Save(false);
        base.SaveChanges();
    }

    private void SetDirty(bool value)
    {
        dirty = value;
        hasUnsavedChanges = value;
    }

    private static long FileTime() => TranslationCsv.Exists ? File.GetLastWriteTimeUtc(TranslationCsv.Path).Ticks : 0;

    private void RefreshUses()
    {
        _uses = new Dictionary<string, List<DialogueUse>>();
        foreach (string guid in AssetDatabase.FindAssets("t:DialogueSequence"))
        {
            var seq = AssetDatabase.LoadAssetAtPath<DialogueSequence>(AssetDatabase.GUIDToAssetPath(guid));
            if (seq == null || seq.lines == null) continue;
            for (int i = 0; i < seq.lines.Count; i++)
            {
                int index = i;
                seq.lines[i].CollectSpokenTexts((text, speaker) =>
                {
                    if (!_uses.TryGetValue(text, out var list)) _uses[text] = list = new List<DialogueUse>();
                    list.Add(new DialogueUse { sequence = seq, index = index, speaker = speaker });
                });
            }
        }
    }

    private void RefreshDuplicates()
    {
        _duplicates = new HashSet<string>(rows.GroupBy(r => r.table + "\n" + r.key)
                                              .Where(g => g.Count() > 1)
                                              .Select(g => g.Key));
    }

    /// <summary>บทพูดที่ยังไม่มีใน CSV → เพิ่มแถวคำแปลว่าง (ต่อท้ายแถว Dialogue ตามลำดับไฟล์บท)</summary>
    public void CheckDialogue()
    {
        RefreshUses();
        var existing = new HashSet<string>(rows.Where(r => r.table == L10n.DialogueTable).Select(r => r.key));
        var missing = _uses.OrderBy(u => AssetDatabase.GetAssetPath(u.Value[0].sequence))
                           .ThenBy(u => u.Value[0].index)
                           .Select(u => u.Key)
                           .Where(k => !existing.Contains(k))
                           .ToList();
        int orphans = rows.Count(IsOrphan);

        if (missing.Count > 0)
        {
            Undo.RecordObject(this, "Add missing dialogue");
            int insertAt = rows.FindLastIndex(r => r.table == L10n.DialogueTable) + 1;
            if (insertAt == 0) insertAt = rows.Count;
            rows.InsertRange(insertAt, missing.Select(k => new TranslationCsv.Row(L10n.DialogueTable, k, "")));
            SetDirty(true);
            RefreshDuplicates();
        }

        if (missing.Count > 0 || orphans > 0)
        {
            filter = Filter.Problems;
            tableIndex = 2;
        }

        string msg = $"บทพูดใหม่ที่ยังไม่มีใน CSV: {missing.Count} บรรทัด" +
                     (missing.Count > 0 ? " (เพิ่มให้แล้ว — กรอกคำแปลแล้วกด Save)" : "") +
                     $"\nแถวที่ไม่มีบทไหนใช้แล้ว: {orphans} แถว";
        if (missing.Count > 0) msg += "\n" + string.Join("\n", missing.Select(k => "  + " + k));
        Debug.Log("[Localization] ตรวจ Dialogue — " + msg);
        ShowNotification(new GUIContent(missing.Count == 0 && orphans == 0
            ? "บทพูดครบทุกบรรทัดแล้ว"
            : $"เพิ่มใหม่ {missing.Count} · ไม่ได้ใช้แล้ว {orphans}"));
    }

    private bool IsOrphan(TranslationCsv.Row r) =>
        r.table == L10n.DialogueTable && _uses != null && !_uses.ContainsKey(r.key);

    private bool IsUntranslated(TranslationCsv.Row r) => string.IsNullOrWhiteSpace(r.th);

    private bool IsDuplicate(TranslationCsv.Row r) => _duplicates != null && _duplicates.Contains(r.table + "\n" + r.key);

    // ───────── GUI ─────────

    private void InitStyles()
    {
        if (_keyStyle != null) return;
        _keyStyle = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = false };
        _thStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true, fontSize = 13 };
        _badgeStyle = new GUIStyle(EditorStyles.miniLabel) { fontStyle = FontStyle.Bold };
    }

    private void OnGUI()
    {
        InitStyles();
        if (_duplicates == null) RefreshDuplicates();

        DrawToolbar();

        if (!TranslationCsv.Exists)
        {
            EditorGUILayout.HelpBox($"ไม่เจอ {TranslationCsv.Path}", MessageType.Error);
            return;
        }

        var visible = rows.Where(Matches).ToList();
        EditorGUILayout.HelpBox("ช่องไทย: ใส่ | ระหว่างคำ = จุดตัดบรรทัด (ในเกมมองไม่เห็น) · ว่าง = ในเกมโชว์อังกฤษ · " +
                                "ช่องอังกฤษคือ key แก้ไม่ได้ — แก้ต้นฉบับแล้วกด ตรวจ Dialogue", MessageType.None);

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        TranslationCsv.Row remove = null;
        foreach (var row in visible)
            if (DrawRow(row)) remove = row;
        if (visible.Count == 0) EditorGUILayout.LabelField("ไม่มีแถวที่ตรงกับตัวกรอง", EditorStyles.centeredGreyMiniLabel);
        EditorGUILayout.EndScrollView();

        if (remove != null)
        {
            Undo.RecordObject(this, "Remove translation row");
            rows.Remove(remove);
            SetDirty(true);
            RefreshDuplicates();
        }
    }

    private void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            if (GUILayout.Button(new GUIContent("Reload", "โหลด CSV ใหม่ (ทิ้งที่แก้ค้างไว้)"), EditorStyles.toolbarButton, GUILayout.Width(55)))
            {
                if (!dirty || EditorUtility.DisplayDialog("Reload", "ทิ้งที่แก้ไว้แล้วโหลด CSV ใหม่?", "ทิ้ง", "ยกเลิก"))
                    Load();
            }
            if (GUILayout.Button(new GUIContent("ตรวจ Dialogue", "เพิ่มบทพูดที่ยังไม่มีใน CSV + หาแถวที่ไม่มีบทใช้แล้ว"),
                                 EditorStyles.toolbarButton, GUILayout.Width(90)))
                CheckDialogue();

            GUILayout.Space(6);
            search = GUILayout.TextField(search, EditorStyles.toolbarSearchField, GUILayout.MinWidth(80));
            tableIndex = EditorGUILayout.Popup(tableIndex, TableOptions, EditorStyles.toolbarPopup, GUILayout.Width(75));
            filter = (Filter)EditorGUILayout.Popup((int)filter, FilterLabels, EditorStyles.toolbarPopup, GUILayout.Width(75));

            int untranslated = rows.Count(IsUntranslated);
            int problems = rows.Count(r => IsUntranslated(r) || IsOrphan(r) || IsDuplicate(r));
            GUILayout.Label($"{rows.Count} แถว · ยังไม่แปล {untranslated} · ปัญหา {problems}", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(!dirty))
            {
                if (GUILayout.Button("Save", EditorStyles.toolbarButton, GUILayout.Width(45))) Save(false);
            }
            if (GUILayout.Button(new GUIContent("Save + Import", "เขียน CSV แล้วส่งเข้า String Table — เห็นผลใน Play mode ทันที"),
                                 EditorStyles.toolbarButton, GUILayout.Width(90)))
                Save(true);
        }
    }

    private bool Matches(TranslationCsv.Row r)
    {
        if (tableIndex > 0 && r.table != TableOptions[tableIndex]) return false;
        if (filter == Filter.Untranslated && !IsUntranslated(r)) return false;
        if (filter == Filter.Problems && !(IsUntranslated(r) || IsOrphan(r) || IsDuplicate(r))) return false;
        if (string.IsNullOrEmpty(search)) return true;
        return r.key.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
            || (r.th ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
            || (_uses != null && _uses.TryGetValue(r.key, out var uses)
                && uses.Any(u => u.sequence.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));
    }

    /// <returns>true = กดลบแถวนี้</returns>
    private bool DrawRow(TranslationCsv.Row row)
    {
        bool remove = false;
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(row.table, EditorStyles.miniBoldLabel, GUILayout.Width(58));
                if (IsUntranslated(row)) Badge("ยังไม่แปล", new Color(0.95f, 0.45f, 0.4f));
                if (IsOrphan(row)) Badge("ไม่มีบทนี้แล้ว", new Color(0.95f, 0.7f, 0.25f));
                if (IsDuplicate(row)) Badge("key ซ้ำ", new Color(0.95f, 0.7f, 0.25f));

                if (_uses != null && _uses.TryGetValue(row.key, out var uses))
                {
                    foreach (var u in uses.Take(3))
                    {
                        var label = new GUIContent($"{u.sequence.name} #{u.index + 1} · {u.speaker}", "คลิก = เลือกไฟล์บทนี้");
                        if (GUILayout.Button(label, EditorStyles.miniButton))
                        {
                            Selection.activeObject = u.sequence;
                            EditorGUIUtility.PingObject(u.sequence);
                        }
                    }
                    if (uses.Count > 3) GUILayout.Label($"+{uses.Count - 3}", EditorStyles.miniLabel);
                }

                GUILayout.FlexibleSpace();
                if (IsOrphan(row) || IsDuplicate(row))
                    remove = GUILayout.Button(new GUIContent("ลบ", "ลบแถวนี้ออกจาก CSV (Ctrl+Z ได้)"), EditorStyles.miniButton, GUILayout.Width(36));
            }

            EditorGUILayout.SelectableLabel(row.key, _keyStyle,
                GUILayout.Height(_keyStyle.CalcHeight(new GUIContent(row.key), position.width - 40)));

            EditorGUI.BeginChangeCheck();
            string th = EditorGUILayout.TextArea(row.th ?? "", _thStyle);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(this, "Edit translation");
                row.th = th;
                SetDirty(true);
            }
        }
        return remove;
    }

    private void Badge(string text, Color color)
    {
        var prev = GUI.contentColor;
        GUI.contentColor = color;
        GUILayout.Label(text, _badgeStyle, GUILayout.ExpandWidth(false));
        GUI.contentColor = prev;
    }
}
