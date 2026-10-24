using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.VFX;

/// <summary>
/// Shader warm-up: วาดทุก variant ใน <see cref="ShaderWarmupList"/> หนึ่งครั้งหน้ากล้องตอนจอดำต้นด่าน
/// (<see cref="CoopSceneStarter"/> รอตัวนี้เสร็จก่อนรายงาน "โหลดเสร็จ") → ไดรเวอร์คอมไพล์ shader ก่อนผู้เล่นเห็น
/// VFX/ของที่โผล่ครั้งแรกกลางเกมจะไม่กระตุก
///
/// วาดในซีนจริง = ได้ variant ตามแสง/เงา/fog ของด่านนั้น (global keyword ของ URP) ไม่ใช่เดาจาก material อย่างเดียว
/// quad ขยับทุกเฟรม → ได้ pass motion vector ด้วย (เกมเปิด TAA/STP ตลอด) · ทอด shadow → ได้ ShadowCaster
/// ภาพล้วน ไม่มี network · รายการค้างในหน่วยความจำข้ามซีน (shader ไม่ถูก unload ระหว่างด่าน)
/// </summary>
public static class ShaderWarmup
{
    private const int MaterialsPerBatch = 48;
    private const int FramesPerBatch = 3;
    private const int FramesPerVfx = 4;
    private const float CameraWaitSeconds = 5f;
    private const float QuadSize = 0.02f;

    private static ShaderWarmupList s_list;
    private static Mesh s_quad;

    /// <summary>กำลัง warm-up อยู่ (กันเรียกซ้อน)</summary>
    public static bool IsRunning { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        s_list = null;
        s_quad = null;
        IsRunning = false;
    }

    /// <summary>coroutine — รอกล้องหลัก (level 02/03 กล้องมากับ prefab) สูงสุด 5 วิ แล้ววาดทีละชุด</summary>
    public static IEnumerator Run()
    {
        if (IsRunning) yield break;
        if (!s_list) s_list = Resources.Load<ShaderWarmupList>(ShaderWarmupList.ResourcePath);
        if (!s_list) yield break;

        IsRunning = true;
        float start = Time.realtimeSinceStartup;

        Camera cam = Camera.main;
        while (!cam && Time.realtimeSinceStartup - start < CameraWaitSeconds)
        {
            yield return null;
            cam = Camera.main;
        }
        if (!cam)
        {
            Debug.LogWarning("[ShaderWarmup] ไม่เจอ Camera.main — ข้าม warm-up");
            IsRunning = false;
            yield break;
        }

        var root = new GameObject("[Shader Warmup]");
        int layer = PickLayer(cam);
        var renderers = CreateRenderers(root.transform, layer);
        var materials = new List<Material>(MaterialsPerBatch);
        int drawn = 0;

        var variants = s_list.variants;
        for (int i = 0; i < variants.Length && root; i += MaterialsPerBatch)
        {
            materials.Clear();
            for (int r = 0; r < renderers.Length; r++)
            {
                int index = i + r;
                bool used = index < variants.Length && variants[index].shader && variants[index].shader.isSupported;
                renderers[r].enabled = used;
                if (!used) continue;

                var mat = new Material(variants[index].shader) { shaderKeywords = variants[index].keywords };
                materials.Add(mat);
                renderers[r].sharedMaterial = mat;
                drawn++;
            }

            for (int f = 0; f < FramesPerBatch && root; f++)
            {
                PlaceInFront(root.transform, cam, f);
                yield return null;
            }

            foreach (var m in materials) Object.Destroy(m);
        }

        int vfxCount = 0;
        if (root)
        {
            foreach (var r in renderers) r.enabled = false;
            foreach (var asset in s_list.visualEffects)
            {
                if (!asset || !root) continue;
                var go = new GameObject(asset.name) { layer = layer };
                go.transform.SetParent(root.transform, false);
                go.transform.localScale = Vector3.one * QuadSize;
                var vfx = go.AddComponent<VisualEffect>();
                vfx.visualEffectAsset = asset;
                vfx.Play();
                vfxCount++;

                for (int f = 0; f < FramesPerVfx && root; f++)
                {
                    PlaceInFront(root.transform, cam, f);
                    yield return null;
                }
                if (go) Object.Destroy(go);
            }
        }

        if (root) Object.Destroy(root);
        IsRunning = false;
        Debug.Log($"[ShaderWarmup] {drawn} variants + {vfxCount} VFX ใน {(Time.realtimeSinceStartup - start) * 1000f:0} ms");
    }

    private static MeshRenderer[] CreateRenderers(Transform root, int layer)
    {
        var mesh = Quad();
        var renderers = new MeshRenderer[MaterialsPerBatch];
        for (int i = 0; i < renderers.Length; i++)
        {
            var go = new GameObject("Quad") { layer = layer };
            go.transform.SetParent(root, false);
            go.transform.localScale = Vector3.one * QuadSize;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.On;
            mr.receiveShadows = true;
            mr.allowOcclusionWhenDynamic = false;   // level 01 อบ occlusion culling ไว้
            mr.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
            mr.enabled = false;
            renderers[i] = mr;
        }
        return renderers;
    }

    /// <summary>หน้ากล้องเลยระยะ near นิดเดียว · เลื่อนสลับไปมาทุกเฟรมให้ URP วาด motion vector</summary>
    private static void PlaceInFront(Transform root, Camera cam, int frame)
    {
        var t = cam.transform;
        float jiggle = (frame & 1) == 0 ? 0.001f : -0.001f;
        root.SetPositionAndRotation(
            t.position + t.forward * (cam.nearClipPlane + 0.5f) + t.right * jiggle,
            t.rotation);
    }

    private static int PickLayer(Camera cam)
    {
        if ((cam.cullingMask & 1) != 0) return 0;
        for (int i = 0; i < 32; i++)
            if ((cam.cullingMask & (1 << i)) != 0) return i;
        return 0;
    }

    /// <summary>quad สองหน้า (วัสดุที่ Cull Back/Front ก็ยังวาด) มี normal/tangent/uv/สี ครบสำหรับ shader ทุกแบบ</summary>
    private static Mesh Quad()
    {
        if (s_quad) return s_quad;
        s_quad = new Mesh { name = "ShaderWarmupQuad" };
        s_quad.SetVertices(new[]
        {
            new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
        });
        s_quad.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
        var tangent = new Vector4(1f, 0f, 0f, 1f);
        s_quad.SetTangents(new[] { tangent, tangent, tangent, tangent });
        var uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
        s_quad.SetUVs(0, uv);
        s_quad.SetUVs(1, uv);
        s_quad.SetColors(new[] { Color.white, Color.white, Color.white, Color.white });
        s_quad.SetTriangles(new[] { 0, 2, 1, 2, 3, 1, 0, 1, 2, 2, 1, 3 }, 0);
        s_quad.RecalculateBounds();
        return s_quad;
    }
}
