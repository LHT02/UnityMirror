// StandalonePlanarMirror.cs
// Built-in Render Pipeline - Planar Reflection (planar camera + RenderTexture).
// MIT-style.

using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(Renderer))]
public class StandalonePlanarMirror : MonoBehaviour
{
    [Header("Mirror Plane")]
    public Transform planeTransformOverride;                 // 可选：自定义平面（用它的位置与朝向）
    public enum NormalSource { Forward, Up, Right, CustomTransformForward }
    public NormalSource normalSource = NormalSource.Forward; // 墙镜: Forward；地镜: Up
    [Tooltip("沿平面法线的微小偏移，避免Z-fight")]
    public float planeOffset = 0.01f;

    [Header("Rendering")]
    [Range(0.1f, 2f)] public float resolutionScale = 1.0f;   // 相对屏幕分辨率比例
    public Vector2Int fixedResolution = Vector2Int.zero;     // 固定分辨率（优先于比例，为0按比例）
    [Range(0, 8)] public int msaa = 2;
    public LayerMask reflectionMask = ~0;                    // 反射相机可见层（可排除镜子自身层）
    public bool enableShadows = false;

    public enum ClearMode { FromReferenceCamera, Skybox, SolidColor, DepthOnly, Nothing }
    [Header("Clear & Background")]
    public ClearMode clearMode = ClearMode.FromReferenceCamera;
    public Color clearColor = Color.black;
    public Material customSkybox;

    [Header("Stereo / XR")]
    [Tooltip("逐眼渲染并分别写入 _ReflectionTex0/_ReflectionTex1")]
    public bool perEyeRenderInVR = true;
    [Tooltip("关闭后只渲染一次，把同一张纹理写入左右眼")]
    public bool singleTextureForBothEyes = false;

    [Header("Performance Tweaks")]
    [Range(0.0f, 1.0f)] public float clipPlaneOffset = 0.07f; // 斜裁剪偏移
    [Tooltip("渲染时临时覆盖像素光数量（<=0不改）")]
    public int overridePixelLightCount = 0;

    [Header("Shader Property Names (fixed)")]
    [SerializeField] private string leftEyeTextureProp  = "_ReflectionTex0";
    [SerializeField] private string rightEyeTextureProp = "_ReflectionTex1";

    private Renderer _renderer;
    private MaterialPropertyBlock _mpb;

    private RenderTexture _rtMono;          // 非立体/共用
    private RenderTexture _rtLeft, _rtRight;// 立体每眼
    private readonly Dictionary<Camera, Camera> _reflectionCameras = new();
    private bool _isRenderingThisCamera;
    private int _oldScreenW, _oldScreenH;

#if UNITY_EDITOR
    private void EditorTick()
    {
        if (!Application.isPlaying && _renderer && _renderer.isVisible)
        {
            var cam = UnityEditor.SceneView.lastActiveSceneView
                      ? UnityEditor.SceneView.lastActiveSceneView.camera
                      : Camera.main;
            if (cam) RenderForCamera(cam);
        }
    }
#endif

    private void OnEnable()
    {
        _renderer = GetComponent<Renderer>();
        _mpb ??= new MaterialPropertyBlock();
        EnsureRTs(Screen.width, Screen.height);
        ApplyTexturesToRenderer();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.update += EditorTick;
#endif
    }

    private void OnDisable()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.update -= EditorTick;
#endif
        CleanupCameras();
        ReleaseRTs();
        if (_renderer)
        {
            // Clear property block instead of setting null textures to avoid ArgumentNullException
            _renderer.SetPropertyBlock(null);
        }
    }

    private void OnWillRenderObject()
    {
        var currentCam = Camera.current;
        if (!currentCam || !_renderer || !_renderer.enabled) return;
        RenderForCamera(currentCam);
    }

    private void LateUpdate()
    {
        if (Screen.width != _oldScreenW || Screen.height != _oldScreenH)
        {
            EnsureRTs(Screen.width, Screen.height);
            ApplyTexturesToRenderer();
        }
    }

    private void RenderForCamera(Camera srcCam)
    {
        if (!isActiveAndEnabled || _isRenderingThisCamera) return;
        _isRenderingThisCamera = true;
        try
        {
            EnsureRTs(srcCam.pixelWidth, srcCam.pixelHeight);

            bool stereo = srcCam.stereoEnabled;
            if (stereo && perEyeRenderInVR && !singleTextureForBothEyes)
            {
                RenderEye(srcCam, Camera.StereoscopicEye.Left,  ref _rtLeft);
                RenderEye(srcCam, Camera.StereoscopicEye.Right, ref _rtRight);
                ApplyTexturesToRenderer();
            }
            else
            {
                RenderMono(srcCam, ref _rtMono);
                ApplyTexturesToRenderer();
            }
        }
        finally { _isRenderingThisCamera = false; }
    }

    // ---------- 渲染实现 ----------
    private void RenderMono(Camera srcCam, ref RenderTexture dstRT)
    {
        var reflCam = GetOrCreateReflectionCamera(srcCam);
        var (planePos, planeNormal) = GetPlaneWorld();

        var d = -Vector3.Dot(planeNormal, planePos) - planeOffset;
        var reflectionPlane = new Vector4(planeNormal.x, planeNormal.y, planeNormal.z, d);
        var reflectionMat = CalculateReflectionMatrix(reflectionPlane);

        Matrix4x4 view = srcCam.worldToCameraMatrix * reflectionMat;
        Matrix4x4 proj = srcCam.projectionMatrix;

        SetupCameraCommon(srcCam, reflCam, view, proj, planePos, planeNormal, reflectionMat);
        DoRender(reflCam, ref dstRT);
    }

    private void RenderEye(Camera srcCam, Camera.StereoscopicEye eye, ref RenderTexture dstRT)
    {
        var reflCam = GetOrCreateReflectionCamera(srcCam);
        var (planePos, planeNormal) = GetPlaneWorld();

        var d = -Vector3.Dot(planeNormal, planePos) - planeOffset;
        var reflectionPlane = new Vector4(planeNormal.x, planeNormal.y, planeNormal.z, d);
        var reflectionMat = CalculateReflectionMatrix(reflectionPlane);

        Matrix4x4 eyeView = srcCam.GetStereoViewMatrix(eye) * reflectionMat;
        Matrix4x4 eyeProj = srcCam.GetStereoProjectionMatrix(eye);

        SetupCameraCommon(srcCam, reflCam, eyeView, eyeProj, planePos, planeNormal, reflectionMat);
        DoRender(reflCam, ref dstRT);
    }

    private void SetupCameraCommon(
        Camera srcCam, Camera reflCam,
        Matrix4x4 view, Matrix4x4 projSrc,
        Vector3 planePos, Vector3 planeNormal, Matrix4x4 reflectionMat)
    {
        reflCam.worldToCameraMatrix = view;

        // 清屏与天空盒
        SetupClearFlagsAndSkybox(srcCam, reflCam);

        // 斜裁剪
        var clipPlaneCameraSpace = CameraSpacePlane(reflCam, planePos, planeNormal, 1.0f);
        var proj = AdjustProjectionForOblique(projSrc, clipPlaneCameraSpace);
        reflCam.projectionMatrix = proj;

        // 同步参数
        reflCam.cullingMask = reflectionMask;
        reflCam.useOcclusionCulling = srcCam.useOcclusionCulling;
        reflCam.allowHDR = srcCam.allowHDR;
        reflCam.allowMSAA = msaa > 0;
        reflCam.nearClipPlane = srcCam.nearClipPlane;
        reflCam.farClipPlane = srcCam.farClipPlane;
        reflCam.orthographic = srcCam.orthographic;
        reflCam.fieldOfView = srcCam.fieldOfView;
        reflCam.orthographicSize = srcCam.orthographicSize;

        // 相机姿态
        var euler = srcCam.transform.eulerAngles;
        reflCam.transform.position = reflectionMat.MultiplyPoint(srcCam.transform.position);
        reflCam.transform.eulerAngles = new Vector3(-euler.x, euler.y, -euler.z);
    }

    private void DoRender(Camera reflCam, ref RenderTexture targetRT)
    {
        EnsureRTs(Screen.width, Screen.height);

        int oldPixelLightCount = QualitySettings.pixelLightCount;
        if (overridePixelLightCount > 0) QualitySettings.pixelLightCount = overridePixelLightCount;

        var oldShadows = QualitySettings.shadows;
        if (!enableShadows) QualitySettings.shadows = ShadowQuality.Disable;

        GL.invertCulling = true;
        var old = RenderTexture.active;

        targetRT = PickRTForTarget(targetRT);
        reflCam.targetTexture = targetRT;
        reflCam.Render();

        RenderTexture.active = old;
        GL.invertCulling = false;

        if (overridePixelLightCount > 0) QualitySettings.pixelLightCount = oldPixelLightCount;
        if (!enableShadows) QualitySettings.shadows = oldShadows;
    }

    private RenderTexture PickRTForTarget(RenderTexture current)
    {
        int w = fixedResolution.x > 0 ? fixedResolution.x : Mathf.Max(1, Mathf.RoundToInt(Screen.width * resolutionScale));
        int h = fixedResolution.y > 0 ? fixedResolution.y : Mathf.Max(1, Mathf.RoundToInt(Screen.height * resolutionScale));
        int aa = Mathf.Max(1, msaa);

        if (current != null && (current.width != w || current.height != h || current.antiAliasing != aa))
        {
            if (current.IsCreated()) current.Release();
            DestroyImmediate(current);
            current = null;
        }
        if (current == null)
        {
            current = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            current.name = $"MirrorRT_{GetInstanceID()}_{w}x{h}_MSAA{aa}";
            current.useMipMap = false;
            current.autoGenerateMips = false;
            current.antiAliasing = aa;
            current.wrapMode = TextureWrapMode.Clamp;
            current.filterMode = FilterMode.Bilinear;
            current.Create();
            _oldScreenW = Screen.width;
            _oldScreenH = Screen.height;
        }
        return current;
    }

    private void EnsureRTs(int _, int __)
    {
        if (_rtMono  != null && !_rtMono.IsCreated())  _rtMono.Create();
        if (_rtLeft  != null && !_rtLeft.IsCreated())  _rtLeft.Create();
        if (_rtRight != null && !_rtRight.IsCreated()) _rtRight.Create();
        _oldScreenW = Screen.width; _oldScreenH = Screen.height;
    }

    private void ReleaseRTs()
    {
        void release(RenderTexture r){ if (r!=null){ if (r.IsCreated()) r.Release(); DestroyImmediate(r); } }
        release(_rtMono);  _rtMono = null;
        release(_rtLeft);  _rtLeft = null;
        release(_rtRight); _rtRight = null;
    }

    private void ApplyTexturesToRenderer()
    {
        if (!_renderer) return;
        _renderer.GetPropertyBlock(_mpb);

        // 非立体/单纹理：左右眼都写同一张RT；逐眼时分别写入
        Texture left  = _rtMono != null ? _rtMono : _rtLeft;
        Texture right = _rtMono != null ? _rtMono : (_rtRight != null ? _rtRight : _rtLeft);

        // Avoid passing null to MaterialPropertyBlock.SetTexture
        if (!string.IsNullOrEmpty(leftEyeTextureProp)  && left  != null) _mpb.SetTexture(leftEyeTextureProp,  left);
        if (!string.IsNullOrEmpty(rightEyeTextureProp) && right != null) _mpb.SetTexture(rightEyeTextureProp, right);

        _renderer.SetPropertyBlock(_mpb);
    }

    // —— 矩阵/平面工具 ——
    private (Vector3 pos, Vector3 normal) GetPlaneWorld()
    {
        Transform t = planeTransformOverride ? planeTransformOverride : transform;
        Vector3 n = normalSource switch
        {
            NormalSource.Up    => t.up,
            NormalSource.Right => t.right,
            NormalSource.CustomTransformForward => (planeTransformOverride ? planeTransformOverride : transform).forward,
            _ => t.forward
        };
        n = n.normalized;
        return (t.position + n * planeOffset, n);
    }

    private static Matrix4x4 CalculateReflectionMatrix(Vector4 p)
    {
        Matrix4x4 m = Matrix4x4.identity;
        m.m00 = (1F - 2F * p[0]*p[0]); m.m01 = (-2F * p[0]*p[1]); m.m02 = (-2F * p[0]*p[2]); m.m03 = (-2F * p[3]*p[0]);
        m.m10 = (-2F * p[1]*p[0]); m.m11 = (1F - 2F * p[1]*p[1]); m.m12 = (-2F * p[1]*p[2]); m.m13 = (-2F * p[3]*p[1]);
        m.m20 = (-2F * p[2]*p[0]); m.m21 = (-2F * p[2]*p[1]); m.m22 = (1F - 2F * p[2]*p[2]); m.m23 = (-2F * p[3]*p[2]);
        m.m30 = 0F; m.m31 = 0F; m.m32 = 0F; m.m33 = 1F; return m;
    }

    private static Vector4 CameraSpacePlane(Camera cam, Vector3 pos, Vector3 normal, float sideSign)
    {
        Matrix4x4 m = cam.worldToCameraMatrix;
        Vector3 cPos = m.MultiplyPoint(pos);
        Vector3 cNormal = m.MultiplyVector(normal).normalized * sideSign;
        return new Vector4(cNormal.x, cNormal.y, cNormal.z, -Vector3.Dot(cPos, cNormal));
    }

    private Matrix4x4 AdjustProjectionForOblique(Matrix4x4 srcProj, Vector4 clipPlaneCameraSpace)
    {
        Vector4 q = srcProj.inverse * new Vector4(
            Mathf.Sign(clipPlaneCameraSpace.x),
            Mathf.Sign(clipPlaneCameraSpace.y),
            1.0f, 1.0f
        );
        Vector4 c = clipPlaneCameraSpace * (2.0f / Vector4.Dot(clipPlaneCameraSpace, q));
        srcProj[2, 0] = c.x; srcProj[2, 1] = c.y; srcProj[2, 2] = c.z + 1.0f; srcProj[2, 3] = c.w;
        return srcProj;
    }

    private void SetupClearFlagsAndSkybox(Camera src, Camera refl)
    {
        switch (clearMode)
        {
            case ClearMode.FromReferenceCamera:
                refl.clearFlags = src.clearFlags;
                refl.backgroundColor = src.backgroundColor;
                CopyOrDisableSkybox(src, refl); break;
            case ClearMode.Skybox:
                refl.clearFlags = CameraClearFlags.Skybox;
                if (customSkybox != null)
                {
                    var sky = refl.GetComponent<Skybox>() ?? refl.gameObject.AddComponent<Skybox>();
                    sky.enabled = true; sky.material = customSkybox;
                }
                else CopyOrDisableSkybox(src, refl);
                break;
            case ClearMode.SolidColor:
                refl.clearFlags = CameraClearFlags.SolidColor;
                refl.backgroundColor = clearColor;
                DisableSkybox(refl); break;
            case ClearMode.DepthOnly:
                refl.clearFlags = CameraClearFlags.Depth;
                DisableSkybox(refl); break;
            case ClearMode.Nothing:
                refl.clearFlags = CameraClearFlags.Nothing;
                DisableSkybox(refl); break;
        }
    }

    private static void CopyOrDisableSkybox(Camera src, Camera dst)
    {
        var srcSky = src.GetComponent<Skybox>();
        if (srcSky && srcSky.material)
        {
            var dstSky = dst.GetComponent<Skybox>() ?? dst.gameObject.AddComponent<Skybox>();
            dstSky.enabled = true; dstSky.material = srcSky.material;
        }
        else DisableSkybox(dst);
    }

    private static void DisableSkybox(Camera cam)
    {
        var sky = cam.GetComponent<Skybox>();
        if (sky) sky.enabled = false;
    }

    private Camera GetOrCreateReflectionCamera(Camera src)
    {
        if (_reflectionCameras.TryGetValue(src, out var cam) && cam) return cam;
        var go = new GameObject($"__MirrorCamera_{GetInstanceID()}_{src.GetInstanceID()}");
        go.hideFlags = HideFlags.HideAndDontSave;
        var newCam = go.AddComponent<Camera>(); newCam.enabled = false;
        _reflectionCameras[src] = newCam; return newCam;
    }

    private void CleanupCameras()
    {
        foreach (var kv in _reflectionCameras)
            if (kv.Value) DestroyImmediate(kv.Value.gameObject);
        _reflectionCameras.Clear();
    }
}
