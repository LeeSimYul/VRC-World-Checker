// ============================================================================
//  VRCWorldPreFlightChecker.cs
//
//  배치 위치 : Assets/Editor/VRCWorldPreFlightChecker.cs
//  대상 환경 : Unity 2022.3 LTS + VRChat SDK3 Worlds (외부 패키지 의존성 없음)
//  메뉴      : Tools > VRC World Pre-Flight Checker
//
//  VRChat 월드 업로드 전에 열려 있는 모든 씬을 6개 카테고리로 진단하고,
//  가능한 항목은 버튼 하나로 자동 수정(Auto-Fix)합니다.
//  씬 전체 직렬화 필드를 훑는 참조 무결성 검사, 애니메이션 바인딩 기반 Static 분석,
//  중복 지오메트리 탐지, 스캔 간 월드 지표 비교와 Markdown 리포트를 제공합니다.
//  씬 수정은 Undo 그룹으로 기록되므로 Ctrl+Z로 되돌릴 수 있습니다.
//  텍스처 임포트 설정(.meta) 축소는 Undo 대신 원본 값을 백업해 [원본 크기로 복원]으로 되돌립니다.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace VRCWorldPreFlight
{
    /// <summary>
    /// VRChat 월드 배포 전 무결성/최적화 진단 및 자동 수정 EditorWindow.
    /// </summary>
    public class VRCWorldPreFlightChecker : EditorWindow
    {
        // ====================================================================
        #region 데이터 모델
        // ====================================================================

        /// <summary>문제 심각도</summary>
        private enum Severity
        {
            Error,   // 업로드 실패·기능 고장으로 이어지는 문제
            Warning, // 성능 저하·잠재적 버그
        }

        /// <summary>검사 카테고리 (Foldout 표시 순서와 동일)</summary>
        private enum Category
        {
            MissingScripts = 0,
            AnimatorStatic = 1,
            MeshColliderScale = 2,
            UIText = 3,
            MirrorTexture = 4,
            References = 5,
        }

        private const int CategoryCount = 6;

        /// <summary>스캔으로 발견된 문제 한 건</summary>
        private sealed class Issue
        {
            public Severity Severity;
            public Object Target;        // Select/Ping 대상 (씬 오브젝트 또는 에셋)
            public string ObjectName;    // 리스트에 표시할 이름
            public string Tooltip;       // 계층 경로 또는 에셋 경로
            public string Description;   // 문제 원인 설명
            public string FixLabel;      // 자동 수정 버튼 라벨
            public string FixConfirm;    // 수정 전 확인 메시지 (null이면 확인 없이 수정)
            public Func<bool> FixAction; // 자동 수정 로직 (null이면 수동 처리 필요)
            public string FixTooltip = DefaultFixTooltip;
            public string AssetPath;     // 텍스처 항목의 에셋 경로 (원본 크기 복원 시 결과 갱신용)
            public long SavedBytes;      // 수정 시 예상되는 메모리 절감량 (대시보드 "수정 효과" 집계용)
            public int SavedDrawCalls;   // 수정 시 예상되는 드로우콜 절감량
            public bool IsFixed;

            // UnityEngine.Object의 == 연산자를 사용하므로 파괴된 오브젝트도 false로 판정됨
            public bool IsAlive => Target != null;
            public bool IsOpen => !IsFixed && IsAlive;
            public bool CanFix => FixAction != null && IsOpen;

            public void SetFix(string label, Func<bool> action, string confirm = null)
            {
                FixLabel = label;
                FixAction = action;
                FixConfirm = confirm;
            }
        }

        /// <summary>애니메이션 클립이 오브젝트에 거는 바인딩 종류</summary>
        [Flags]
        private enum AnimatedKind
        {
            None = 0,
            Transform = 1, // 위치/회전/스케일 → 하위 자식도 함께 움직임
            Active = 2,    // GameObject m_IsActive 토글 → 하위 자식도 함께 꺼짐
            Property = 4,  // 머티리얼·컴포넌트 프로퍼티 → 해당 오브젝트에만 영향
        }

        private const AnimatedKind InheritedAnimatedKinds = AnimatedKind.Transform | AnimatedKind.Active;

        /// <summary>스캔 시점의 월드 지표 (이전 스캔과 비교해 개선 효과를 수치로 보여줌)</summary>
        private sealed class WorldMetrics
        {
            public string SceneKey;      // 같은 씬 구성끼리만 비교하기 위한 키
            public int Errors;
            public int Warnings;
            public long Triangles;       // 활성 렌더러 기준
            public int UniqueMaterials;  // 렌더러 + Skybox에서 쓰는 고유 머티리얼
            public long TextureBytes;    // 씬에서 사용하는 텍스처 메모리 합계 (에디터 기준 추정치)
            public int RealtimeLights;   // 실시간/혼합 라이트
        }

        /// <summary>에셋(텍스처·머티리얼) 사용처 집계</summary>
        private sealed class AssetUsage
        {
            public int Count;
            public string FirstUser;
        }

        #endregion

        // ====================================================================
        #region 상수
        // ====================================================================

        private static readonly string[] k_CategoryTitles =
        {
            "1. Missing Scripts",
            "2. Animator & Static 충돌",
            "3. MeshCollider & Negative Scale",
            "4. Legacy UI Text & 깨진 TMP",
            "5. 거울(Mirror) & 고해상도 텍스처",
            "6. 참조 무결성 (Missing Reference)",
        };

        private static readonly string[] k_CategoryHelp =
        {
            "열린 씬의 모든 GameObject(비활성 포함)에서 누락된 스크립트를 찾습니다. VRChat 업로드 실패와 Udon 오류의 가장 흔한 원인입니다.",
            "Animator가 있거나 Animator가 움직이는 오브젝트가 Static이면 정적 배칭·라이트맵 처리 때문에 애니메이션이 고정되거나 조명이 어긋납니다.",
            "음수 스케일(노멀·콜라이더 반전), 피벗과 Bounds 중심의 비정상적인 불일치, 메시가 없거나 지나치게 무거운 MeshCollider, 완전히 겹친 중복 메시(Z-fighting·드로우콜 낭비)를 찾습니다.",
            "Legacy UI Text(Dynamic Font 텍스처 4096 초과 위험)와 Atlas 텍스처가 없는 TMP(검은 사각형 버그)를 찾습니다.",
            "기본 활성화된 거울과 과도한 반사 설정, 허용 크기를 넘는 고해상도 텍스처(씬에서 실제로 사용 중인 것만)를 찾습니다.",
            "모든 컴포넌트의 직렬화 필드를 훑어 Missing 참조(Udon 공개 변수·UI 이벤트 포함), 원본이 사라진 프리팹, 프로그램이 없는 UdonBehaviour, 빈 머티리얼 슬롯과 셰이더를 찾을 수 없는(분홍색) 머티리얼을 찾습니다.",
        };

        private static readonly string[] k_BulkFixLabels =
        {
            "일괄 제거", "일괄 Static 해제", "일괄 수정", "일괄 수정", "일괄 수정", "일괄 수정",
        };

        // 텍스처 크기 제한 시 함께 낮출 플랫폼 오버라이드 (PC / Quest(Android) / iOS)
        private static readonly string[] k_TexturePlatforms = { "Standalone", "Android", "iPhone" };

        private static readonly int[] k_TextureSizeOptions = { 512, 1024, 2048, 4096 };

        private static readonly GUIContent[] k_TextureSizeLabels =
        {
            new GUIContent("512"), new GUIContent("1024"), new GUIContent("2048"), new GUIContent("4096"),
        };

        // VRChat SDK에 컴파일 의존성을 만들지 않도록 거울 컴포넌트는 타입 이름으로 식별
        private static readonly HashSet<string> k_MirrorTypeNames = new HashSet<string>
        {
            "VRC_MirrorReflection", "VRCMirrorReflection", "MirrorReflection",
        };

        // 참조 무결성 검사에서 제외할 내부 직렬화 필드 (자기 자신·프리팹 연결 정보)
        private static readonly HashSet<string> k_IgnoredReferenceProperties = new HashSet<string>
        {
            "m_GameObject", "m_Script", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset",
            "m_PrefabInternal", "m_PrefabParentObject", "m_Father",
        };

        // 오브젝트 참조가 들어 있을 수 없는 원시 타입 배열 (버텍스·바이트 등 대용량 배열 순회를 건너뜀)
        private static readonly HashSet<string> k_PrimitiveArrayElementTypes = new HashSet<string>
        {
            "int", "float", "double", "bool", "char", "string", "UInt8", "SInt8", "UInt16", "SInt16", "UInt32", "SInt32",
            "UInt64", "SInt64", "Vector2", "Vector3", "Vector4", "Vector2f", "Vector3f", "Vector4f", "Quaternion",
            "Quaternionf", "Color", "ColorRGBA", "Matrix4x4", "Matrix4x4f",
        };

        private const string ErrorShaderName = "Hidden/InternalErrorShader";
        private const string MainTexProperty = "_MainTex";
        private const string PrefsPrefix = "VRCWorldPreFlight.";
        private const string DefaultSizeBackupKey = PrefsPrefix + "TextureSizeBackup.Default";
        private const string PlatformSizeBackupKey = PrefsPrefix + "TextureSizeBackup.Platform";
        private const string DefaultFixTooltip = "자동 수정 (Fix) — Ctrl+Z로 되돌릴 수 있습니다.";
        private const string LogPrefix = "[VRC Pre-Flight] ";
        private const string ProgressTitle = "VRC World Pre-Flight Checker";
        private const int DisplayPageSize = 100;
        private const int HugeTextureSize = 8192;

        private const float DefaultBoundsOffset = 20f;
        private const int DefaultMeshColliderTriangles = 20000;
        private const int DefaultTextureMaxSize = 2048;

        #endregion

        // ====================================================================
        #region 상태
        // ====================================================================

        // 스캔 결과: Func 델리게이트를 포함하므로 직렬화하지 않음 (스크립트 리컴파일 시 초기화됨)
        private List<Issue>[] _issues;
        private int[] _displayLimits;
        private HashSet<GameObject> _scannedObjects = new HashSet<GameObject>();
        private bool _hasScanned;
        private bool _isStale;
        private string _staleReason;
        private int _scannedSceneCount;
        private int _scannedObjectCount;
        private double _lastScanSeconds;
        private string _lastScanTime;
        private WorldMetrics _metrics;
        private WorldMetrics _previousMetrics;
        // 다른 GameObject가 참조 중인 씬 오브젝트 (중복 지오메트리 자동 삭제 시 참조가 끊기지 않도록 사용)
        private readonly HashSet<GameObject> _externallyReferencedObjects = new HashSet<GameObject>();

        // UI 상태 (창을 다시 열거나 리컴파일해도 유지)
        [SerializeField] private bool[] _foldouts = { true, true, true, true, true, true };
        [SerializeField] private bool _settingsFoldout;
        [SerializeField] private bool _showErrors = true;
        [SerializeField] private bool _showWarnings = true;
        [SerializeField] private bool _hideFixed;
        [SerializeField] private Vector2 _scroll;

        // 검사 설정 (EditorPrefs에 저장)
        private float _boundsOffsetThreshold = DefaultBoundsOffset;
        private int _meshColliderTriangleThreshold = DefaultMeshColliderTriangles;
        private int _textureMaxSize = DefaultTextureMaxSize;
        private bool _skipEditorOnly = true;

        // 텍스처 Max Size 원본 백업 (에디터 세션 동안 유지)
        // 임포트 설정은 .meta를 직접 바꾸므로 Undo 대신 이 백업으로 [원본 크기로 복원 (Restore)] 합니다.
        private static readonly Dictionary<string, int> s_OriginalMaxTextureSizes = new Dictionary<string, int>();   // 에셋 경로 → 기존 maxTextureSize
        private static readonly Dictionary<string, int> s_OriginalPlatformMaxSizes = new Dictionary<string, int>();  // "경로|플랫폼" → 기존 오버라이드 maxTextureSize
        private static bool s_TextureBackupsLoaded;

        private static readonly Dictionary<Type, bool> s_MirrorTypeCache = new Dictionary<Type, bool>();
        private static readonly Dictionary<string, AnimatedKind> s_NoAnimatedPaths = new Dictionary<string, AnimatedKind>();
        private static readonly List<Texture> s_NoTextures = new List<Texture>();
        private static readonly List<Component> s_ComponentBuffer = new List<Component>();

        // GUI 스타일 (EditorStyles는 OnGUI 안에서만 안전하므로 지연 생성)
        private GUIStyle _titleStyle;
        private GUIStyle _bigButtonStyle;
        private GUIStyle _foldoutStyle;
        private GUIStyle _nameStyle;
        private GUIStyle _wrapStyle;
        private GUIStyle _helpStyle;
        private GUIStyle _tileLabelStyle;
        private GUIStyle _errorNumberStyle;
        private GUIStyle _warningNumberStyle;
        private GUIStyle _okNumberStyle;
        private GUIStyle _errorBadgeStyle;
        private GUIStyle _warningBadgeStyle;
        private GUIStyle _okBadgeStyle;
        private GUIContent _errorIcon;
        private GUIContent _warningIcon;
        private bool _stylesForProSkin;

        #endregion

        // ====================================================================
        #region 창 생명주기
        // ====================================================================

        [MenuItem("Tools/VRC World Pre-Flight Checker")]
        public static void Open()
        {
            var window = GetWindow<VRCWorldPreFlightChecker>();
            window.titleContent = new GUIContent("VRC Pre-Flight", "VRC World Pre-Flight Checker");
            window.minSize = new Vector2(640f, 460f);
            window.Show();
        }

        private void OnEnable()
        {
            EnsureInitialized();
            LoadSettings();
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneClosed -= OnSceneClosed;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        private void EnsureInitialized()
        {
            if (_issues == null || _issues.Length != CategoryCount)
            {
                _issues = new List<Issue>[CategoryCount];
                for (int i = 0; i < CategoryCount; i++)
                    _issues[i] = new List<Issue>();
            }

            if (_displayLimits == null || _displayLimits.Length != CategoryCount)
            {
                _displayLimits = new int[CategoryCount];
                ResetDisplayLimits();
            }

            if (_foldouts == null || _foldouts.Length != CategoryCount)
                _foldouts = new[] { true, true, true, true, true, true };
        }

        private void ResetDisplayLimits()
        {
            for (int i = 0; i < CategoryCount; i++)
                _displayLimits[i] = DisplayPageSize;
        }

        private void OnUndoRedoPerformed()
        {
            MarkStale("Undo/Redo가 수행되어 일부 결과가 실제 상태와 다를 수 있습니다.");
        }

        private void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            MarkStale($"씬 '{scene.name}'이(가) 열려 씬 구성이 변경되었습니다.");
        }

        private void OnSceneClosed(Scene scene)
        {
            MarkStale($"씬 '{scene.name}'이(가) 닫혀 씬 구성이 변경되었습니다.");
        }

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredEditMode)
                MarkStale("플레이 모드가 종료되어 씬 오브젝트가 다시 로드되었습니다.");
            Repaint();
        }

        private void MarkStale(string reason)
        {
            if (!_hasScanned)
                return;
            _isStale = true;
            _staleReason = reason;
            Repaint();
        }

        /// <summary>
        /// 대화상자·진행바·리스트 변경이 IMGUI 레이아웃 도중에 일어나지 않도록
        /// 다음 에디터 업데이트로 작업을 미룹니다.
        /// </summary>
        private void Defer(Action action)
        {
            EditorApplication.delayCall += () =>
            {
                if (this == null)
                    return;
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
                Repaint();
            };
        }

        #endregion

        // ====================================================================
        #region 설정 저장/불러오기
        // ====================================================================

        private void LoadSettings()
        {
            _boundsOffsetThreshold = EditorPrefs.GetFloat(PrefsPrefix + "BoundsOffset", DefaultBoundsOffset);
            _meshColliderTriangleThreshold = EditorPrefs.GetInt(PrefsPrefix + "MeshColliderTriangles", DefaultMeshColliderTriangles);
            _textureMaxSize = EditorPrefs.GetInt(PrefsPrefix + "TextureMaxSize", DefaultTextureMaxSize);
            _skipEditorOnly = EditorPrefs.GetBool(PrefsPrefix + "SkipEditorOnly", true);

            if (Array.IndexOf(k_TextureSizeOptions, _textureMaxSize) < 0)
                _textureMaxSize = DefaultTextureMaxSize;
        }

        private void SaveSettings()
        {
            EditorPrefs.SetFloat(PrefsPrefix + "BoundsOffset", _boundsOffsetThreshold);
            EditorPrefs.SetInt(PrefsPrefix + "MeshColliderTriangles", _meshColliderTriangleThreshold);
            EditorPrefs.SetInt(PrefsPrefix + "TextureMaxSize", _textureMaxSize);
            EditorPrefs.SetBool(PrefsPrefix + "SkipEditorOnly", _skipEditorOnly);
        }

        #endregion

        // ====================================================================
        #region GUI
        // ====================================================================

        private void OnGUI()
        {
            EnsureInitialized();
            EnsureStyles();

            bool isPlaying = EditorApplication.isPlayingOrWillChangePlaymode;

            DrawHeader(isPlaying);
            DrawDashboard();
            DrawSettings();
            DrawFilterBar();

            // 결과 리스트 전체를 스크롤 뷰로 감쌈
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (int i = 0; i < CategoryCount; i++)
                DrawCategory((Category)i, isPlaying);
            EditorGUILayout.EndScrollView();

            DrawFooter(isPlaying);
        }

        private void EnsureStyles()
        {
            bool pro = EditorGUIUtility.isProSkin;
            if (_titleStyle != null && _stylesForProSkin == pro)
                return;
            _stylesForProSkin = pro;

            Color errorColor = pro ? new Color(1f, 0.45f, 0.45f) : new Color(0.75f, 0.1f, 0.1f);
            Color warningColor = pro ? new Color(1f, 0.8f, 0.3f) : new Color(0.65f, 0.42f, 0f);
            Color okColor = pro ? new Color(0.45f, 0.9f, 0.5f) : new Color(0.1f, 0.5f, 0.15f);

            _titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
            _bigButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = 14, fontStyle = FontStyle.Bold };
            _foldoutStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            _nameStyle = new GUIStyle(EditorStyles.boldLabel) { clipping = TextClipping.Clip, wordWrap = false };
            _wrapStyle = new GUIStyle(EditorStyles.label) { wordWrap = true };
            _helpStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel);
            _tileLabelStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel);

            _errorNumberStyle = CreateNumberStyle(errorColor);
            _warningNumberStyle = CreateNumberStyle(warningColor);
            _okNumberStyle = CreateNumberStyle(okColor);

            _errorBadgeStyle = CreateBadgeStyle(errorColor);
            _warningBadgeStyle = CreateBadgeStyle(warningColor);
            _okBadgeStyle = CreateBadgeStyle(okColor);

            _errorIcon = EditorGUIUtility.IconContent("console.erroricon.sml");
            _warningIcon = EditorGUIUtility.IconContent("console.warnicon.sml");
        }

        private static GUIStyle CreateNumberStyle(Color color)
        {
            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 22,
                alignment = TextAnchor.MiddleCenter,
                fixedHeight = 30f,
            };
            style.normal.textColor = color;
            return style;
        }

        private static GUIStyle CreateBadgeStyle(Color color)
        {
            var style = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter };
            style.normal.textColor = color;
            return style;
        }

        // ---------------------------------------------------------------- 상단

        private void DrawHeader(bool isPlaying)
        {
            EditorGUILayout.Space(6f);
            GUILayout.Label("VRChat World Pre-Flight Checker", _titleStyle);
            GUILayout.Label("월드 업로드 전, 무결성과 최적화 상태를 진단하고 자동 수정합니다.", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.Space(4f);

            using (new EditorGUI.DisabledScope(isPlaying))
            {
                Color previous = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.35f, 0.75f, 1f);
                var content = new GUIContent("전체 월드 스캔 실행 (Run Full Scan)", "열려 있는 모든 씬을 5개 카테고리로 검사합니다.");
                if (GUILayout.Button(content, _bigButtonStyle, GUILayout.Height(44f)))
                    Defer(RunFullScan);
                GUI.backgroundColor = previous;
            }

            if (isPlaying)
                EditorGUILayout.HelpBox("플레이 모드에서는 스캔과 자동 수정이 비활성화됩니다. 플레이 모드를 종료한 뒤 실행하세요.", MessageType.Info);

            if (_isStale)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox(_staleReason + " 결과를 갱신하려면 다시 스캔하세요.", MessageType.Warning);
                using (new EditorGUI.DisabledScope(isPlaying))
                {
                    if (GUILayout.Button("다시 스캔", GUILayout.Width(80f), GUILayout.Height(38f)))
                        Defer(RunFullScan);
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        // ---------------------------------------------------------------- 요약 대시보드

        private void DrawDashboard()
        {
            CountIssues(out int errors, out int warnings, out int fixedCount);

            EditorGUILayout.Space(2f);
            EditorGUILayout.BeginHorizontal();
            DrawCountTile("오류 (Error)", _hasScanned ? errors.ToString() : "-", _errorNumberStyle);
            DrawCountTile("경고 (Warning)", _hasScanned ? warnings.ToString() : "-", _warningNumberStyle);
            DrawCountTile("자동 수정 완료 (Fixed)", _hasScanned ? fixedCount.ToString() : "-", _okNumberStyle);
            EditorGUILayout.EndHorizontal();

            if (!_hasScanned)
            {
                EditorGUILayout.HelpBox("아직 스캔하지 않았습니다. 상단의 [전체 월드 스캔 실행] 버튼을 눌러 주세요.", MessageType.Info);
                return;
            }

            GUILayout.Label(
                $"마지막 스캔 {_lastScanTime}  ·  씬 {_scannedSceneCount}개  ·  오브젝트 {_scannedObjectCount:N0}개  ·  {_lastScanSeconds:F2}초",
                EditorStyles.centeredGreyMiniLabel);
            DrawWorldMetrics();

            if (errors == 0 && warnings == 0)
                EditorGUILayout.HelpBox("모든 검사를 통과했습니다. 업로드 준비가 완료되었습니다!", MessageType.Info);
            else if (errors > 0)
                EditorGUILayout.HelpBox($"업로드 전에 반드시 해결해야 할 오류가 {errors}개 있습니다. (경고 {warnings}개)", MessageType.Error);
            else
                EditorGUILayout.HelpBox($"오류는 없지만 최적화 경고가 {warnings}개 있습니다.", MessageType.Warning);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent("리포트 복사", "스캔 결과와 월드 지표를 Markdown으로 클립보드에 복사합니다. (이슈·PR·팀 공유용)"),
                    EditorStyles.miniButtonLeft, GUILayout.Width(90f)))
                CopyReport();
            if (GUILayout.Button(new GUIContent("리포트 저장 (.md)", "스캔 결과와 월드 지표를 Markdown 파일로 저장합니다."),
                    EditorStyles.miniButtonRight, GUILayout.Width(110f)))
                Defer(SaveReport);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>월드 지표 + 이전 스캔 대비 변화 + 이번 자동 수정의 예상 절감 효과</summary>
        private void DrawWorldMetrics()
        {
            if (_metrics == null)
                return;

            GUILayout.Label(
                $"월드 지표  ·  삼각형 {_metrics.Triangles:N0}  ·  머티리얼 {_metrics.UniqueMaterials}종  ·  텍스처 메모리 약 {ToMegabytes(_metrics.TextureBytes):F1} MB  ·  실시간/혼합 라이트 {_metrics.RealtimeLights}개",
                _helpStyle);

            if (_previousMetrics != null)
                GUILayout.Label("이전 스캔 대비  ·  " + DescribeMetricChanges(_previousMetrics, _metrics), _helpStyle);

            long savedBytes = 0;
            int savedDrawCalls = 0;
            foreach (List<Issue> list in _issues)
            {
                foreach (Issue issue in list)
                {
                    if (!issue.IsFixed)
                        continue;
                    savedBytes += issue.SavedBytes;
                    savedDrawCalls += issue.SavedDrawCalls;
                }
            }
            if (savedBytes > 0 || savedDrawCalls > 0)
            {
                GUILayout.Label(
                    $"자동 수정 효과(예상)  ·  텍스처 메모리 약 -{ToMegabytes(savedBytes):F1} MB  ·  드로우콜 -{savedDrawCalls}  (다시 스캔하면 실제 지표로 갱신)",
                    _helpStyle);
            }
        }

        private static string DescribeMetricChanges(WorldMetrics before, WorldMetrics after)
        {
            return $"오류 {before.Errors} → {after.Errors}  ·  경고 {before.Warnings} → {after.Warnings}  ·  " +
                   $"텍스처 메모리 {ToMegabytes(before.TextureBytes):F1} → {ToMegabytes(after.TextureBytes):F1} MB  ·  " +
                   $"삼각형 {before.Triangles:N0} → {after.Triangles:N0}  ·  머티리얼 {before.UniqueMaterials} → {after.UniqueMaterials}종";
        }

        private static float ToMegabytes(long bytes)
        {
            return bytes / (1024f * 1024f);
        }

        private void DrawCountTile(string label, string value, GUIStyle numberStyle)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.MinWidth(120f), GUILayout.ExpandWidth(true));
            GUILayout.Label(value, numberStyle);
            GUILayout.Label(label, _tileLabelStyle);
            EditorGUILayout.EndVertical();
        }

        // ---------------------------------------------------------------- 설정 / 필터

        private void DrawSettings()
        {
            _settingsFoldout = EditorGUILayout.Foldout(_settingsFoldout, "검사 설정 (임계값)", true);
            if (!_settingsFoldout)
                return;

            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();

            _boundsOffsetThreshold = Mathf.Max(1f, EditorGUILayout.FloatField(
                new GUIContent("Bounds 오프셋 임계값 (m)", "MeshRenderer Bounds 중심이 피벗에서 이 거리 이상, 그리고 Bounds 반경보다 멀리 떨어져 있으면 경고합니다."),
                _boundsOffsetThreshold));

            _meshColliderTriangleThreshold = Mathf.Max(100, EditorGUILayout.IntField(
                new GUIContent("MeshCollider 삼각형 경고 기준", "Convex가 아닌 MeshCollider의 삼각형 수가 이 값을 넘으면 경고합니다."),
                _meshColliderTriangleThreshold));

            _textureMaxSize = EditorGUILayout.IntPopup(
                new GUIContent("텍스처 최대 허용 크기 (px)", "가로/세로 중 큰 값이 이 크기를 넘는 텍스처를 경고하며, 자동 수정 시 Max Size를 이 값으로 제한합니다."),
                _textureMaxSize, k_TextureSizeLabels, k_TextureSizeOptions);

            _skipEditorOnly = EditorGUILayout.Toggle(
                new GUIContent("EditorOnly 태그 제외", "빌드에서 제거되는 EditorOnly 태그 오브젝트와 그 하위는 검사하지 않습니다."),
                _skipEditorOnly);

            if (EditorGUI.EndChangeCheck())
            {
                SaveSettings();
                MarkStale("검사 설정이 변경되었습니다.");
            }

            EditorGUI.indentLevel--;
        }

        private void DrawFilterBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            _showErrors = GUILayout.Toggle(_showErrors, new GUIContent(" 오류", _errorIcon.image, "오류 항목 표시"), EditorStyles.toolbarButton, GUILayout.Width(64f));
            _showWarnings = GUILayout.Toggle(_showWarnings, new GUIContent(" 경고", _warningIcon.image, "경고 항목 표시"), EditorStyles.toolbarButton, GUILayout.Width(64f));
            _hideFixed = GUILayout.Toggle(_hideFixed, "수정된 항목 숨기기", EditorStyles.toolbarButton, GUILayout.Width(120f));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("모두 펼치기", EditorStyles.toolbarButton, GUILayout.Width(80f)))
                SetAllFoldouts(true);
            if (GUILayout.Button("모두 접기", EditorStyles.toolbarButton, GUILayout.Width(70f)))
                SetAllFoldouts(false);
            EditorGUILayout.EndHorizontal();
        }

        private void SetAllFoldouts(bool expanded)
        {
            for (int i = 0; i < CategoryCount; i++)
                _foldouts[i] = expanded;
        }

        private bool PassesFilter(Issue issue)
        {
            if (_hideFixed && issue.IsFixed)
                return false;
            return issue.Severity == Severity.Error ? _showErrors : _showWarnings;
        }

        // ---------------------------------------------------------------- 카테고리 (Foldout)

        private void DrawCategory(Category category, bool isPlaying)
        {
            int index = (int)category;
            List<Issue> list = _issues[index];

            int errors = 0, warnings = 0, fixedCount = 0;
            CountList(list, ref errors, ref warnings, ref fixedCount);
            int fixable = 0;
            foreach (Issue issue in list)
            {
                if (issue.CanFix)
                    fixable++;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // 헤더: Foldout + 개수 배지 + 일괄 수정 버튼
            EditorGUILayout.BeginHorizontal();
            Rect foldoutRect = GUILayoutUtility.GetRect(GUIContent.none, _foldoutStyle, GUILayout.ExpandWidth(true), GUILayout.Height(20f));
            _foldouts[index] = EditorGUI.Foldout(foldoutRect, _foldouts[index], k_CategoryTitles[index], true, _foldoutStyle);

            if (!_hasScanned)
            {
                DrawBadge("스캔 전", EditorStyles.centeredGreyMiniLabel);
            }
            else if (errors == 0 && warnings == 0)
            {
                DrawBadge("통과", _okBadgeStyle);
            }
            else
            {
                if (errors > 0)
                    DrawBadge($"오류 {errors}", _errorBadgeStyle);
                if (warnings > 0)
                    DrawBadge($"경고 {warnings}", _warningBadgeStyle);
            }

            using (new EditorGUI.DisabledScope(isPlaying || fixable == 0))
            {
                string bulkTooltip = category == Category.MirrorTexture
                    ? "이 카테고리에서 자동 수정 가능한 항목을 모두 수정합니다. (텍스처 축소는 Undo 대신 [원본 크기로 복원]으로 되돌림)"
                    : "이 카테고리에서 자동 수정 가능한 항목을 모두 수정합니다. (하나의 Undo 단계로 기록)";
                var bulkContent = new GUIContent($"{k_BulkFixLabels[index]} ({fixable})", bulkTooltip);
                if (GUILayout.Button(bulkContent, GUILayout.Width(150f), GUILayout.Height(20f)))
                    Defer(() => BulkFix(category));
            }
            EditorGUILayout.EndHorizontal();

            if (_foldouts[index])
            {
                GUILayout.Label(k_CategoryHelp[index], _helpStyle);
                if (category == Category.MirrorTexture)
                    DrawTextureBackups(isPlaying);

                if (!_hasScanned)
                {
                    GUILayout.Label("스캔 결과가 여기에 표시됩니다.", EditorStyles.centeredGreyMiniLabel);
                }
                else
                {
                    int drawn = 0;
                    int hidden = 0;
                    foreach (Issue issue in list)
                    {
                        if (!PassesFilter(issue))
                            continue;
                        if (drawn >= _displayLimits[index])
                        {
                            hidden++;
                            continue;
                        }
                        DrawIssueRow(issue, isPlaying);
                        drawn++;
                    }

                    if (drawn == 0)
                    {
                        string message = list.Count == 0 ? "문제가 발견되지 않았습니다." : "현재 필터 조건에 해당하는 항목이 없습니다.";
                        EditorGUILayout.HelpBox(message, MessageType.Info);
                    }

                    if (hidden > 0 && GUILayout.Button($"{hidden}개 항목 더 보기 (+{DisplayPageSize})"))
                        _displayLimits[index] += DisplayPageSize;
                }
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>축소한 텍스처의 원본 Max Size 백업 목록 + [원본 크기로 복원 (Restore)] 버튼</summary>
        private void DrawTextureBackups(bool isPlaying)
        {
            LoadTextureBackups();
            if (s_OriginalMaxTextureSizes.Count == 0)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"축소한 텍스처 원본 백업 ({s_OriginalMaxTextureSizes.Count}개) — 에디터를 닫기 전까지 유지", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(isPlaying))
            {
                if (GUILayout.Button("모두 복원", GUILayout.Width(80f)))
                    Defer(RestoreAllTextures);
            }
            EditorGUILayout.EndHorizontal();

            foreach (string path in s_OriginalMaxTextureSizes.Keys)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent(System.IO.Path.GetFileName(path), path), _nameStyle, GUILayout.Width(170f));
                GUILayout.Label($"원본: {DescribeTextureBackup(path)}", _wrapStyle, GUILayout.MinWidth(120f), GUILayout.ExpandWidth(true));
                if (GUILayout.Button(new GUIContent("Select", "Project에서 선택하고 Ping 합니다."), GUILayout.Width(60f)))
                    SelectAndPing(AssetDatabase.LoadMainAssetAtPath(path));
                using (new EditorGUI.DisabledScope(isPlaying))
                {
                    if (GUILayout.Button("원본 크기로 복원 (Restore)", GUILayout.Width(170f)))
                        Defer(() => RestoreTexture(path));
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        private static void DrawBadge(string text, GUIStyle style)
        {
            var content = new GUIContent(text);
            GUILayout.Label(content, style, GUILayout.Width(style.CalcSize(content).x + 6f), GUILayout.Height(20f));
        }

        // ---------------------------------------------------------------- 결과 행

        /// <summary>[아이콘] [오브젝트 이름] [문제 원인 설명] [Select] [Fix] 를 가로로 배치</summary>
        private void DrawIssueRow(Issue issue, bool isPlaying)
        {
            bool alive = issue.IsAlive;

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            GUILayout.Label(issue.Severity == Severity.Error ? _errorIcon : _warningIcon, GUILayout.Width(20f), GUILayout.Height(18f));

            using (new EditorGUI.DisabledScope(issue.IsFixed || !alive))
            {
                GUILayout.Label(new GUIContent(issue.ObjectName, issue.Tooltip), _nameStyle, GUILayout.Width(170f));
                string description = alive || issue.IsFixed ? issue.Description : "(오브젝트가 삭제되었거나 다시 로드되었습니다 — 다시 스캔하세요)";
                GUILayout.Label(description, _wrapStyle, GUILayout.MinWidth(120f), GUILayout.ExpandWidth(true));
            }

            using (new EditorGUI.DisabledScope(!alive))
            {
                if (GUILayout.Button(new GUIContent("Select", "Hierarchy/Project에서 선택하고 Ping 합니다."), GUILayout.Width(60f)))
                    SelectAndPing(issue.Target);
            }

            if (issue.IsFixed)
            {
                GUILayout.Label("수정됨", _okBadgeStyle, GUILayout.Width(96f));
            }
            else if (issue.FixAction != null)
            {
                using (new EditorGUI.DisabledScope(!alive || isPlaying))
                {
                    var fixContent = new GUIContent(issue.FixLabel, issue.FixTooltip);
                    if (GUILayout.Button(fixContent, GUILayout.Width(96f)))
                        Defer(() => ApplySingleFix(issue));
                }
            }
            else
            {
                GUILayout.Label(new GUIContent("수동 처리", "자동 수정을 지원하지 않는 항목입니다. 설명을 참고해 직접 수정하세요."),
                    EditorStyles.centeredGreyMiniLabel, GUILayout.Width(96f));
            }

            EditorGUILayout.EndHorizontal();
        }

        private static void SelectAndPing(Object target)
        {
            if (target == null)
                return;
            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
        }

        // ---------------------------------------------------------------- 하단

        private void DrawFooter(bool isPlaying)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUILayout.Label("텍스처 축소를 제외한 자동 수정은 Undo(Ctrl+Z)로 되돌릴 수 있습니다. (텍스처는 [원본 크기로 복원]) 수정 후에는 저장하세요.",
                EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(isPlaying || !AnyOpenSceneDirty()))
            {
                if (GUILayout.Button(new GUIContent("씬·에셋 저장", "열린 씬과 수정된 에셋(TMP 머티리얼 등)을 저장합니다."), GUILayout.Width(100f)))
                {
                    Defer(() =>
                    {
                        EditorSceneManager.SaveOpenScenes();
                        AssetDatabase.SaveAssets();
                    });
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private static bool AnyOpenSceneDirty()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.isDirty)
                    return true;
            }
            return false;
        }

        #endregion

        // ====================================================================
        #region 집계
        // ====================================================================

        private void CountIssues(out int errors, out int warnings, out int fixedCount)
        {
            errors = 0;
            warnings = 0;
            fixedCount = 0;
            foreach (List<Issue> list in _issues)
                CountList(list, ref errors, ref warnings, ref fixedCount);
        }

        /// <summary>미해결(수정 전 + 오브젝트 존재) 항목만 오류/경고로 집계</summary>
        private static void CountList(List<Issue> list, ref int errors, ref int warnings, ref int fixedCount)
        {
            foreach (Issue issue in list)
            {
                if (issue.IsFixed)
                {
                    fixedCount++;
                    continue;
                }
                if (!issue.IsAlive)
                    continue;
                if (issue.Severity == Severity.Error)
                    errors++;
                else
                    warnings++;
            }
        }

        #endregion

        // ====================================================================
        #region 스캔
        // ====================================================================

        private void RunFullScan()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            foreach (List<Issue> list in _issues)
                list.Clear();
            ResetDisplayLimits();
            _externallyReferencedObjects.Clear();
            if (_metrics != null)
                _previousMetrics = _metrics;
            _metrics = new WorldMetrics { SceneKey = string.Join(";", GetLoadedSceneNames()) };
            if (_previousMetrics != null && _previousMetrics.SceneKey != _metrics.SceneKey)
                _previousMetrics = null; // 다른 씬 구성과는 비교하지 않음

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                EditorUtility.DisplayProgressBar(ProgressTitle, "씬 오브젝트 수집 중...", 0f);
                List<GameObject> objects = CollectSceneGameObjects(out _scannedSceneCount);
                _scannedObjectCount = objects.Count;
                _scannedObjects = new HashSet<GameObject>(objects);

                EditorUtility.DisplayProgressBar(ProgressTitle, "1. Missing Scripts 검사 중...", 0.05f);
                ScanMissingScripts(objects);

                EditorUtility.DisplayProgressBar(ProgressTitle, "2. Animator & Static 충돌 검사 중...", 0.1f);
                ScanAnimatorStatic(objects);

                // 참조 무결성을 먼저 검사해 '다른 오브젝트가 참조 중인 오브젝트' 색인을 만든 뒤, 중복 지오메트리 삭제 안전성 판단에 사용
                ScanReferenceIntegrity(objects, 0.15f, 0.55f);

                EditorUtility.DisplayProgressBar(ProgressTitle, "3. MeshCollider & Negative Scale · 중복 지오메트리 검사 중...", 0.6f);
                ScanMeshColliderAndScale(objects);
                ScanDuplicateGeometry(objects);

                EditorUtility.DisplayProgressBar(ProgressTitle, "4. Legacy UI Text & TMP 검사 중...", 0.7f);
                ScanUIText(objects);

                EditorUtility.DisplayProgressBar(ProgressTitle, "5. 거울 & 고해상도 텍스처 검사 중...", 0.8f);
                ScanMirrors(objects);
                ScanHighResolutionTextures(objects);

                EditorUtility.DisplayProgressBar(ProgressTitle, "월드 지표 집계 중...", 0.95f);
                CollectWorldMetrics(objects);
            }
            catch (Exception e)
            {
                Debug.LogError(LogPrefix + "스캔 도중 예외가 발생해 결과가 일부만 표시될 수 있습니다.");
                Debug.LogException(e);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            stopwatch.Stop();
            _lastScanSeconds = stopwatch.Elapsed.TotalSeconds;
            _lastScanTime = DateTime.Now.ToString("HH:mm:ss");
            _hasScanned = true;
            _isStale = false;

            CountIssues(out int errors, out int warnings, out _);
            _metrics.Errors = errors;
            _metrics.Warnings = warnings;
            Debug.Log($"{LogPrefix}스캔 완료 — 오류 {errors}개, 경고 {warnings}개 (오브젝트 {_scannedObjectCount:N0}개, {_lastScanSeconds:F2}초)");
        }

        /// <summary>로드된 모든 씬의 GameObject를 비활성 포함으로 수집</summary>
        private List<GameObject> CollectSceneGameObjects(out int sceneCount)
        {
            var result = new List<GameObject>(4096);
            var roots = new List<GameObject>();
            sceneCount = 0;

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;

                sceneCount++;
                scene.GetRootGameObjects(roots);
                foreach (GameObject root in roots)
                    CollectRecursive(root.transform, result);
            }
            return result;
        }

        private void CollectRecursive(Transform transform, List<GameObject> result)
        {
            GameObject go = transform.gameObject;

            // EditorOnly 태그 오브젝트는 하위까지 빌드에서 제거되므로 검사 제외 (옵션)
            if (_skipEditorOnly && go.CompareTag("EditorOnly"))
                return;
            // 빌드에 저장되지 않는 에디터 전용 숨김 오브젝트 제외
            if ((go.hideFlags & HideFlags.DontSaveInBuild) != 0)
                return;

            result.Add(go);
            for (int i = 0; i < transform.childCount; i++)
                CollectRecursive(transform.GetChild(i), result);
        }

        private Issue AddIssue(Category category, Severity severity, GameObject go, string description)
        {
            return AddIssue(category, severity, go, go.name, GetHierarchyPath(go), description);
        }

        private Issue AddIssue(Category category, Severity severity, Object target, string objectName, string tooltip, string description)
        {
            var issue = new Issue
            {
                Severity = severity,
                Target = target,
                ObjectName = objectName,
                Tooltip = tooltip,
                Description = description,
            };
            _issues[(int)category].Add(issue);
            return issue;
        }

        // ---------------------------------------------------------------- 1. Missing Scripts

        private void ScanMissingScripts(List<GameObject> objects)
        {
            foreach (GameObject go in objects)
            {
                int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (missing <= 0)
                    continue;

                // 프리팹 인스턴스라면 원본 프리팹 에셋에 포함된 Missing Script 수를 분리
                int fromPrefabAsset = 0;
                string prefabPath = null;
                if (PrefabUtility.IsPartOfPrefabInstance(go))
                {
                    GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(go);
                    if (source != null)
                        fromPrefabAsset = Mathf.Min(missing, GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(source));
                    prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                }

                var description = new StringBuilder();
                description.Append($"Missing Script {missing}개 — VRChat 업로드 실패와 Udon 오류의 주요 원인입니다. 스크립트를 복구하거나 컴포넌트를 제거하세요.");
                if (fromPrefabAsset > 0)
                    description.Append($"\n※ 이 중 {fromPrefabAsset}개는 원본 프리팹('{prefabPath}')에 포함되어 있어, 프리팹 에셋을 열어 직접 제거해야 합니다.");

                Issue issue = AddIssue(Category.MissingScripts, Severity.Error, go, description.ToString());
                if (missing - fromPrefabAsset > 0)
                    issue.SetFix("제거", () => RemoveMissingScripts(issue, go));
            }
        }

        private static bool RemoveMissingScripts(Issue issue, GameObject go)
        {
            if (go == null)
                return false;

            // Missing Script 제거는 RecordObject로 복원되지 않으므로 오브젝트 전체 상태를 기록
            Undo.RegisterCompleteObjectUndo(go, "Missing Script 제거");
            int removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
            MarkModified(go);

            int remaining = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
            if (remaining == 0)
                return true;

            // 원본 프리팹에 속한 Missing Script는 인스턴스에서 제거되지 않음 → 수동 처리로 전환
            string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            issue.Description = $"Missing Script {removed}개 제거됨, {remaining}개 남음 — 원본 프리팹('{prefabPath}')을 열어 직접 제거하세요.";
            issue.FixAction = null;
            Debug.LogWarning($"{LogPrefix}'{go.name}': Missing Script {remaining}개가 원본 프리팹('{prefabPath}')에 있어 제거되지 않았습니다.", go);
            return false;
        }

        // ---------------------------------------------------------------- 2. Animator & Static 충돌

        private void ScanAnimatorStatic(List<GameObject> objects)
        {
            var reported = new HashSet<GameObject>();
            var pathCache = new Dictionary<RuntimeAnimatorController, Dictionary<string, AnimatedKind>>();

            foreach (GameObject go in objects)
            {
                Animator animator = go.GetComponent<Animator>();
                if (animator == null)
                    continue;

                Dictionary<string, AnimatedKind> animatedPaths = GetAnimatedPaths(animator.runtimeAnimatorController, pathCache);

                // (A) Animator 루트 본체가 Static인 경우 (하위 자식을 애니메이션하는 루트 포함)
                if (go.isStatic && reported.Add(go))
                    AddAnimatorStaticIssue(go, animator, true, GetAffectingKinds(string.Empty, animatedPaths), HasChildPaths(animatedPaths));

                if (animatedPaths.Count == 0)
                    continue;

                // (B) 클립이 직접 애니메이션하는 경로의 오브젝트(Transform·m_IsActive·머티리얼/컴포넌트 프로퍼티)와,
                //     Transform 이동/m_IsActive 토글을 부모로부터 함께 받는 하위 오브젝트가 Static인 경우
                foreach (Transform child in go.GetComponentsInChildren<Transform>(true))
                {
                    GameObject childGo = child.gameObject;
                    if (childGo == go || !childGo.isStatic || reported.Contains(childGo) || !_scannedObjects.Contains(childGo))
                        continue;

                    string relativePath = AnimationUtility.CalculateTransformPath(child, go.transform);
                    AnimatedKind kinds = GetAffectingKinds(relativePath, animatedPaths);
                    if (kinds == AnimatedKind.None)
                        continue;

                    reported.Add(childGo);
                    AddAnimatorStaticIssue(childGo, animator, false, kinds, false);
                }
            }
        }

        private void AddAnimatorStaticIssue(GameObject go, Animator animator, bool isAnimatorRoot, AnimatedKind kinds, bool animatesChildren)
        {
            StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(go);
            bool batchingStatic = (flags & StaticEditorFlags.BatchingStatic) != 0;
            bool occluderStatic = (flags & StaticEditorFlags.OccluderStatic) != 0;
            bool contributeGI = (flags & StaticEditorFlags.ContributeGI) != 0;
            bool moves = (kinds & AnimatedKind.Transform) != 0;
            bool toggles = (kinds & AnimatedKind.Active) != 0;

            var description = new StringBuilder();
            if (isAnimatorRoot)
            {
                description.Append("Animator 컴포넌트가 있는 오브젝트가 Static으로 지정됨");
                if (animatesChildren)
                    description.Append(" (하위 자식을 애니메이션하는 루트)");
            }
            else
            {
                description.Append($"Animator '{animator.name}'의 애니메이션 대상 오브젝트가 Static으로 지정됨");
            }
            if (kinds != AnimatedKind.None)
                description.Append($" — 애니메이션: {DescribeAnimatedKinds(kinds)}");

            bool isError = false;
            bool hasSpecificReason = false;
            if (batchingStatic && (moves || isAnimatorRoot))
            {
                description.Append("\n• Batching Static: 빌드 시 메시가 병합·고정되어 이동/회전/스케일 애니메이션이 반영되지 않습니다.");
                isError = true;
                hasSpecificReason = true;
            }
            if (occluderStatic && toggles)
            {
                description.Append("\n• Occluder Static: 오브젝트를 꺼도 구워진 오클루전 데이터가 남아 뒤쪽 오브젝트가 사라져 보일 수 있습니다.");
                isError = true;
                hasSpecificReason = true;
            }
            if (contributeGI && (moves || toggles))
            {
                description.Append("\n• Contribute GI: 움직이거나 꺼져도 라이트맵에 구워진 그림자·반사광이 그대로 남습니다.");
                hasSpecificReason = true;
            }
            if (!hasSpecificReason)
                description.Append("\n• 라이트맵·오클루전·배칭이 고정 상태 기준으로 계산되어 애니메이션 결과와 어긋날 수 있습니다.");

            Issue issue = AddIssue(Category.AnimatorStatic, isError ? Severity.Error : Severity.Warning, go, description.ToString());
            issue.SetFix("Static 해제", () =>
            {
                if (go == null)
                    return false;
                Undo.RecordObject(go, "Static 해제");
                go.isStatic = false;
                MarkModified(go);
                return true;
            });
        }

        /// <summary>
        /// 컨트롤러의 모든 클립에서 애니메이션 대상 경로(Animator 기준 상대 경로)와 바인딩 종류를 수집합니다.
        /// float 커브(Transform·m_IsActive·머티리얼/컴포넌트 수치)와 오브젝트 참조 커브(머티리얼·스프라이트 교체)를 모두 분석합니다.
        /// </summary>
        private static Dictionary<string, AnimatedKind> GetAnimatedPaths(RuntimeAnimatorController controller,
            Dictionary<RuntimeAnimatorController, Dictionary<string, AnimatedKind>> cache)
        {
            if (controller == null)
                return s_NoAnimatedPaths;
            if (cache.TryGetValue(controller, out Dictionary<string, AnimatedKind> cached))
                return cached;

            var paths = new Dictionary<string, AnimatedKind>();
            var visitedClips = new HashSet<AnimationClip>();
            foreach (AnimationClip clip in controller.animationClips)
            {
                if (clip == null || !visitedClips.Add(clip))
                    continue;

                // Humanoid 클립은 Animator 타입의 머슬 커브로 본(Transform) 계층 전체를 움직임
                if (clip.humanMotion)
                    AddAnimatedKind(paths, string.Empty, AnimatedKind.Transform);

                foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
                    AddAnimatedKind(paths, binding.path, ClassifyBinding(binding));
                foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    AddAnimatedKind(paths, binding.path, ClassifyBinding(binding));
            }

            cache[controller] = paths;
            return paths;
        }

        private static AnimatedKind ClassifyBinding(EditorCurveBinding binding)
        {
            if (binding.type != null && typeof(Transform).IsAssignableFrom(binding.type))
                return AnimatedKind.Transform;
            if (binding.type == typeof(GameObject))
                return AnimatedKind.Active; // GameObject에서 애니메이션 가능한 속성은 m_IsActive뿐
            return AnimatedKind.Property;
        }

        private static void AddAnimatedKind(Dictionary<string, AnimatedKind> paths, string path, AnimatedKind kind)
        {
            paths.TryGetValue(path, out AnimatedKind existing);
            paths[path] = existing | kind;
        }

        /// <summary>
        /// 경로 자신에 걸린 모든 바인딩 + 상위 경로의 Transform 이동/m_IsActive 토글(하위로 전파됨)을 합산.
        /// 머티리얼·컴포넌트 프로퍼티는 해당 오브젝트에만 영향을 주므로 전파하지 않음.
        /// </summary>
        private static AnimatedKind GetAffectingKinds(string relativePath, Dictionary<string, AnimatedKind> animatedPaths)
        {
            animatedPaths.TryGetValue(relativePath, out AnimatedKind kinds);
            foreach (KeyValuePair<string, AnimatedKind> pair in animatedPaths)
            {
                if (IsAncestorPath(pair.Key, relativePath))
                    kinds |= pair.Value & InheritedAnimatedKinds;
            }
            return kinds;
        }

        private static bool IsAncestorPath(string ancestor, string path)
        {
            if (ancestor.Length == 0)
                return path.Length > 0; // 빈 경로 = Animator 루트
            return path.Length > ancestor.Length
                && path[ancestor.Length] == '/'
                && path.StartsWith(ancestor, StringComparison.Ordinal);
        }

        private static bool HasChildPaths(Dictionary<string, AnimatedKind> animatedPaths)
        {
            foreach (string path in animatedPaths.Keys)
            {
                if (path.Length > 0)
                    return true;
            }
            return false;
        }

        private static string DescribeAnimatedKinds(AnimatedKind kinds)
        {
            var parts = new List<string>(3);
            if ((kinds & AnimatedKind.Transform) != 0)
                parts.Add("Transform 이동");
            if ((kinds & AnimatedKind.Active) != 0)
                parts.Add("활성화(m_IsActive) 토글");
            if ((kinds & AnimatedKind.Property) != 0)
                parts.Add("머티리얼/컴포넌트 프로퍼티");
            return string.Join(", ", parts);
        }

        // ---------------------------------------------------------------- 3. MeshCollider & Negative Scale / Bounds

        private void ScanMeshColliderAndScale(List<GameObject> objects)
        {
            foreach (GameObject go in objects)
            {
                Renderer renderer = go.GetComponent<Renderer>();
                Collider collider = go.GetComponent<Collider>();
                MeshCollider meshCollider = go.GetComponent<MeshCollider>();

                // (A) 음수 스케일: 메시 렌더러나 콜라이더가 있는 오브젝트만 실제 문제가 됨 (스프라이트 반전 등은 제외)
                bool hasMeshRenderer = renderer is MeshRenderer || renderer is SkinnedMeshRenderer;
                Vector3 lossyScale = go.transform.lossyScale;
                if ((hasMeshRenderer || collider != null) && HasNegativeComponent(lossyScale))
                    AddNegativeScaleIssue(go, lossyScale, meshCollider != null, collider != null);

                // (B) MeshRenderer Bounds 중심과 피벗의 비정상적인 거리
                MeshRenderer meshRenderer = renderer as MeshRenderer;
                if (meshRenderer != null)
                    CheckBoundsOffset(meshRenderer);

                // (C) MeshCollider 메시 누락 / 고폴리곤
                if (meshCollider != null)
                    CheckMeshCollider(meshCollider);
            }
        }

        private void AddNegativeScaleIssue(GameObject go, Vector3 lossyScale, bool hasMeshCollider, bool hasCollider)
        {
            Transform source = FindNegativeScaleSource(go.transform);
            bool inherited = source != null && source != go.transform;

            var description = new StringBuilder();
            description.Append($"음수 스케일 {FormatVector(lossyScale)} — 노멀·폴리곤 면 방향이 뒤집혀 조명/그림자/컬링 오류");
            if (hasMeshCollider)
                description.Append(", MeshCollider 충돌면 반전(플레이어 통과·끼임) 및 런타임 쿠킹 비용");
            else if (hasCollider)
                description.Append(", 콜라이더 판정 이상");
            description.Append(" 위험이 있습니다.");
            if (inherited)
                description.Append($"\n부모 '{source.name}'의 음수 스케일을 상속 중입니다.");

            Issue issue = AddIssue(Category.MeshColliderScale, Severity.Warning, go, description.ToString());
            if (source == null)
                return;

            string confirm = inherited
                ? $"부모 '{source.name}'의 localScale을 양수로 변환합니다.\n그 하위의 모든 오브젝트가 함께 반전되어 보일 수 있습니다.\n\n계속하시겠습니까?"
                : $"'{go.name}'의 localScale을 양수로 변환합니다.\n대칭이 아닌 메시는 좌우(또는 상하)가 반전되어 보입니다. 미러링이 필요하다면 모델링 툴에서 반전된 메시를 export하는 것을 권장합니다.\n\n계속하시겠습니까?";

            issue.SetFix("스케일 양수화", () =>
            {
                if (go == null)
                    return false;
                // 수정 시점에 다시 탐색하므로 음수 스케일 조상이 여러 개여도 반복 수정 가능
                Transform target = FindNegativeScaleSource(go.transform);
                if (target == null)
                    return true;
                Undo.RecordObject(target, "음수 스케일 양수화");
                Vector3 s = target.localScale;
                target.localScale = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
                MarkModified(target);
                return !HasNegativeComponent(go.transform.lossyScale);
            }, confirm);
        }

        /// <summary>자신 또는 가장 가까운 조상 중 localScale에 음수 성분이 있는 Transform</summary>
        private static Transform FindNegativeScaleSource(Transform transform)
        {
            for (Transform t = transform; t != null; t = t.parent)
            {
                if (HasNegativeComponent(t.localScale))
                    return t;
            }
            return null;
        }

        private static bool HasNegativeComponent(Vector3 v)
        {
            return v.x < 0f || v.y < 0f || v.z < 0f;
        }

        private void CheckBoundsOffset(MeshRenderer meshRenderer)
        {
            MeshFilter meshFilter = meshRenderer.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null)
                return;

            Transform transform = meshRenderer.transform;
            Vector3 center;
            float radius;
            if (meshRenderer.enabled && meshRenderer.gameObject.activeInHierarchy)
            {
                Bounds bounds = meshRenderer.bounds;
                center = bounds.center;
                radius = bounds.extents.magnitude;
            }
            else
            {
                // 비활성 렌더러는 월드 Bounds가 갱신되지 않으므로 메시 로컬 Bounds를 월드로 변환해 근사
                Bounds local = meshFilter.sharedMesh.bounds;
                center = transform.TransformPoint(local.center);
                radius = Vector3.Scale(local.extents, transform.lossyScale).magnitude;
            }

            // 임계값을 넘고, 피벗이 메시 Bounds 구(sphere) 바깥에 있을 때만 비정상으로 판단
            float offset = Vector3.Distance(center, transform.position);
            if (offset <= _boundsOffsetThreshold || offset <= radius)
                return;

            AddIssue(Category.MeshColliderScale, Severity.Warning, meshRenderer.gameObject,
                $"MeshRenderer Bounds 중심이 피벗에서 {offset:F1}m 떨어져 있음 (Bounds 반경 {radius:F1}m) — 메시 원점이 잘못 export된 것으로 보입니다.\n" +
                "라이트/리플렉션 프로브 샘플링, 투명 정렬, 오클루전 컬링 오류의 원인이 되므로 모델링 툴에서 원점(피벗)을 재설정하세요.");
        }

        private void CheckMeshCollider(MeshCollider meshCollider)
        {
            GameObject go = meshCollider.gameObject;

            if (meshCollider.sharedMesh == null)
            {
                MeshFilter meshFilter = go.GetComponent<MeshFilter>();
                Mesh candidate = meshFilter != null ? meshFilter.sharedMesh : null;

                string description = "MeshCollider에 메시(Shared Mesh)가 지정되지 않음 — 충돌 판정이 전혀 없어 플레이어가 통과하거나 추락합니다.";
                description += candidate != null
                    ? $"\nMeshFilter의 '{candidate.name}' 메시를 자동으로 할당할 수 있습니다."
                    : "\n할당할 메시를 수동으로 지정하세요.";

                Issue issue = AddIssue(Category.MeshColliderScale, Severity.Error, go, description);
                if (candidate != null)
                {
                    issue.SetFix("메시 할당", () =>
                    {
                        if (meshCollider == null || candidate == null)
                            return false;
                        Undo.RecordObject(meshCollider, "MeshCollider 메시 할당");
                        meshCollider.sharedMesh = candidate;
                        MarkModified(meshCollider);
                        return true;
                    });
                }
                return;
            }

            if (meshCollider.convex)
                return;

            int triangles = GetTriangleCount(meshCollider.sharedMesh);
            if (triangles > _meshColliderTriangleThreshold)
            {
                AddIssue(Category.MeshColliderScale, Severity.Warning, go,
                    $"고폴리곤 MeshCollider ({triangles:N0} 삼각형, 기준 {_meshColliderTriangleThreshold:N0}) — 물리 연산·메모리 비용이 큽니다.\n" +
                    "Box/Capsule Collider 조합이나 단순화한 충돌 전용 메시 사용을 권장합니다.");
            }
        }

        /// <summary>Read/Write 비활성 메시에서도 동작하도록 인덱스 수로 삼각형 수를 계산</summary>
        private static int GetTriangleCount(Mesh mesh)
        {
            long indices = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                if (mesh.GetTopology(i) == MeshTopology.Triangles)
                    indices += mesh.GetIndexCount(i);
            }
            return (int)(indices / 3);
        }

        // ---------------------------------------------------------------- 4. Legacy UI Text & 깨진 TMP

        private void ScanUIText(List<GameObject> objects)
        {
            var legacyTexts = new List<Text>();
            var tmpTexts = new List<TMP_Text>();
            TMP_FontAsset defaultTmpFont = GetDefaultTmpFont();

            foreach (GameObject go in objects)
            {
                go.GetComponents(legacyTexts);
                foreach (Text text in legacyTexts)
                {
                    if (text != null)
                        AddLegacyTextIssue(text);
                }

                go.GetComponents(tmpTexts);
                foreach (TMP_Text tmp in tmpTexts)
                {
                    if (tmp != null)
                        CheckTmpText(tmp, defaultTmpFont);
                }
            }
        }

        private void AddLegacyTextIssue(Text text)
        {
            var description = new StringBuilder("Dynamic Font 4096 텍스처 초과 위험 (TMP 전환 권장)");
            description.Append($"\nLegacy UI Text(Font Size {text.fontSize}");
            if (text.resizeTextForBestFit)
                description.Append(", Best Fit 사용 중 — 위험 증가");
            description.Append(")는 한글 등 다국어 문자를 큰 크기로 그릴 때 폰트 텍스처가 4096을 넘어 글자가 깨지거나 사라질 수 있습니다.");
            description.Append("\nUdon 스크립트가 이 Text를 참조할 수 있어 자동 변환하지 않습니다. TextMeshPro로 교체 후 참조를 다시 연결하세요.");

            AddIssue(Category.UIText, Severity.Warning, text.gameObject, description.ToString());
        }

        private void CheckTmpText(TMP_Text tmp, TMP_FontAsset defaultFont)
        {
            TMP_FontAsset font = tmp.font;
            Material material = tmp.fontSharedMaterial;
            Texture2D atlas = font != null ? GetFontAtlas(font) : null;

            string problem;
            if (font == null)
                problem = "TMP Font Asset이 지정되지 않음";
            else if (material == null)
                problem = "TMP 머티리얼(fontSharedMaterial)이 없음";
            else if (!material.HasProperty(MainTexProperty))
                problem = $"머티리얼 '{material.name}'의 셰이더가 누락되었거나 TMP 셰이더가 아님";
            else if (material.mainTexture == null)
                problem = $"머티리얼 '{material.name}'의 mainTexture(폰트 Atlas)가 null";
            else
                return;

            Issue issue = AddIssue(Category.UIText, Severity.Error, tmp.gameObject,
                problem + " — 글자 대신 검은 사각형이 표시되거나 텍스트가 보이지 않는 상태입니다.");

            if (font == null)
            {
                if (defaultFont != null)
                    issue.SetFix("기본 폰트 지정", () => AssignTmpFont(tmp, defaultFont));
                else
                    issue.Description += "\nTMP Settings에 기본 폰트가 없어 자동 수정할 수 없습니다. (Window > TextMeshPro > Import TMP Essential Resources)";
            }
            else if (material != null && atlas != null && material.HasProperty(MainTexProperty) && MatchesAtlasSize(material, atlas))
            {
                // 1순위: 현재 머티리얼(커스텀 프리셋 포함)의 _MainTex에 폰트 Atlas를 직접 재연결 → Outline/Underlay 등 셰이더 설정 보존
                issue.SetFix("Atlas 재연결", () => RelinkFontAtlas(tmp, material),
                    $"머티리얼 '{material.name}'의 _MainTex에 폰트 '{font.name}'의 Atlas 텍스처를 다시 연결합니다.\nOutline/Underlay 등 머티리얼 설정은 그대로 유지되며, 공유 에셋이므로 이 머티리얼을 쓰는 모든 텍스트에 적용됩니다.\n\n계속하시겠습니까?");
            }
            else if (IsValidTmpMaterial(font.material))
            {
                // 2순위: 현재 머티리얼에 재연결할 수 없으면(셰이더 누락·Atlas 없음·다른 폰트용 프리셋) 폰트 기본 머티리얼로 교체
                if (material != null && atlas != null && material.HasProperty(MainTexProperty))
                    issue.Description += $"\n머티리얼 '{material.name}'에 기록된 Atlas 크기가 폰트 '{font.name}'과 달라(다른 폰트용 프리셋) Atlas를 재연결하지 않습니다.";
                string confirm = material != null
                    ? $"깨진 머티리얼 '{material.name}' 대신 폰트 기본 머티리얼 '{font.material.name}'을(를) 사용합니다.\n외곽선·그림자 등 머티리얼 프리셋 설정은 적용되지 않습니다.\n\n계속하시겠습니까?"
                    : null;
                issue.SetFix("머티리얼 복구", () => RestoreTmpMaterial(tmp), confirm);
            }
            else if (font.material != null && font.material.HasProperty(MainTexProperty) && atlas != null)
            {
                // 3순위: 폰트 기본 머티리얼 자체가 Atlas 참조를 잃은 경우 → 기본 머티리얼에 Atlas 재연결 후 사용
                Material fontMaterial = font.material;
                issue.Description += $"\n폰트 에셋 '{font.name}'의 기본 머티리얼도 Atlas 텍스처 참조를 잃었습니다.";
                issue.SetFix("Atlas 재연결", () => RelinkFontAtlas(tmp, fontMaterial),
                    $"폰트 에셋 '{font.name}'의 기본 머티리얼 '{fontMaterial.name}'에 Atlas 텍스처를 다시 연결합니다.\n공유 에셋이므로 이 폰트를 쓰는 모든 텍스트에 적용됩니다.\n\n계속하시겠습니까?");
            }
            else
            {
                issue.Description += $"\n폰트 에셋 '{font.name}'의 기본 머티리얼/Atlas도 손상되어 있습니다. Font Asset Creator로 폰트 에셋을 다시 생성하세요.";
            }
        }

        private static bool IsValidTmpMaterial(Material material)
        {
            return material != null && material.HasProperty(MainTexProperty) && material.mainTexture != null;
        }

        /// <summary>
        /// font.atlasTexture를 안전하게 가져옵니다.
        /// (atlasTexture 게터는 내부 Atlas 배열이 비어 있으면 예외를 던질 수 있으므로 배열을 먼저 확인)
        /// </summary>
        private static Texture2D GetFontAtlas(TMP_FontAsset font)
        {
            Texture2D[] atlases = font.atlasTextures;
            if (atlases == null || atlases.Length == 0 || atlases[0] == null)
                return null;
            return font.atlasTexture;
        }

        /// <summary>
        /// TMP 셰이더는 Atlas 크기를 _TextureWidth/_TextureHeight에 기록합니다.
        /// 값이 다르면 다른 폰트용 머티리얼 프리셋으로 보고 재연결하지 않습니다 (글리프 UV 불일치로 글자가 깨지는 것을 방지).
        /// </summary>
        private static bool MatchesAtlasSize(Material material, Texture2D atlas)
        {
            if (!material.HasProperty("_TextureWidth") || !material.HasProperty("_TextureHeight"))
                return true;
            return Mathf.Approximately(material.GetFloat("_TextureWidth"), atlas.width)
                && Mathf.Approximately(material.GetFloat("_TextureHeight"), atlas.height);
        }

        private static TMP_FontAsset GetDefaultTmpFont()
        {
            // TMP Settings 에셋이 없을 때 TMP_Settings에 접근하면 임포터 창이 뜰 수 있으므로 존재 여부를 먼저 확인
            TMP_Settings settings = Resources.Load<TMP_Settings>("TMP Settings");
            return settings != null ? TMP_Settings.defaultFontAsset : null;
        }

        private static bool AssignTmpFont(TMP_Text tmp, TMP_FontAsset font)
        {
            if (tmp == null || font == null)
                return false;

            Undo.RecordObject(tmp, "TMP 기본 폰트 지정");
            tmp.font = font;
            if (!IsValidTmpMaterial(tmp.fontSharedMaterial))
                tmp.fontSharedMaterial = font.material;
            MarkModified(tmp);
            return IsValidTmpMaterial(tmp.fontSharedMaterial);
        }

        private static bool RestoreTmpMaterial(TMP_Text tmp)
        {
            if (tmp == null || tmp.font == null)
                return false;

            Undo.RecordObject(tmp, "TMP 머티리얼 복구");
            tmp.fontSharedMaterial = tmp.font.material;
            MarkModified(tmp);
            return IsValidTmpMaterial(tmp.fontSharedMaterial);
        }

        /// <summary>대상 머티리얼의 _MainTex에 폰트 Atlas를 연결하고, TMP가 그 머티리얼을 쓰도록 보장</summary>
        private static bool RelinkFontAtlas(TMP_Text tmp, Material targetMaterial)
        {
            if (tmp == null || tmp.font == null || targetMaterial == null)
                return false;

            Texture2D atlas = GetFontAtlas(tmp.font);
            if (atlas == null)
                return false;

            // 머티리얼 에셋 수정도 Undo로 기록되므로 Ctrl+Z로 되돌릴 수 있음
            Undo.RecordObject(targetMaterial, "TMP 폰트 Atlas 재연결");
            targetMaterial.SetTexture(MainTexProperty, atlas);
            EditorUtility.SetDirty(targetMaterial);

            if (tmp.fontSharedMaterial != targetMaterial)
            {
                Undo.RecordObject(tmp, "TMP 폰트 Atlas 재연결");
                tmp.fontSharedMaterial = targetMaterial;
            }
            MarkModified(tmp);
            return IsValidTmpMaterial(tmp.fontSharedMaterial);
        }

        // ---------------------------------------------------------------- 5-A. 거울(Mirror)

        private void ScanMirrors(List<GameObject> objects)
        {
            var behaviours = new List<MonoBehaviour>();
            foreach (GameObject go in objects)
            {
                go.GetComponents(behaviours);
                foreach (MonoBehaviour behaviour in behaviours)
                {
                    // Missing Script 슬롯은 null로 반환됨
                    if (behaviour != null && IsMirrorComponent(behaviour))
                        CheckMirror(behaviour);
                }
            }
        }

        private static bool IsMirrorComponent(MonoBehaviour behaviour)
        {
            Type type = behaviour.GetType();
            if (s_MirrorTypeCache.TryGetValue(type, out bool isMirror))
                return isMirror;

            isMirror = false;
            for (Type t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                if (k_MirrorTypeNames.Contains(t.Name))
                {
                    isMirror = true;
                    break;
                }
            }
            s_MirrorTypeCache[type] = isMirror;
            return isMirror;
        }

        private void CheckMirror(MonoBehaviour mirror)
        {
            GameObject go = mirror.gameObject;

            // (A) 기본 활성화 상태: 입장 즉시 씬을 한 번 더 렌더링
            if (go.activeInHierarchy && mirror.enabled)
            {
                Issue issue = AddIssue(Category.MirrorTexture, Severity.Warning, go,
                    "거울이 기본 활성화(ON) 상태 — 입장 즉시 씬 전체를 한 번 더 렌더링해 프레임이 크게 떨어집니다. 기본 OFF + 토글 버튼 사용을 권장합니다.");
                issue.SetFix("기본 비활성화", () =>
                {
                    if (go == null)
                        return false;
                    Undo.RecordObject(go, "거울 기본 비활성화");
                    go.SetActive(false);
                    MarkModified(go);
                    return true;
                }, $"거울 오브젝트 '{go.name}'을(를) 비활성화합니다.\n플레이어가 거울을 켤 수 있는 토글(Udon) 버튼이 월드에 있는지 확인하세요.\n\n계속하시겠습니까?");
            }

            // VRCMirrorReflection / VRC_MirrorReflection의 직렬화 필드는 리플렉션 대신 SerializedObject로 읽음
            var serialized = new SerializedObject(mirror);

            // (B) Reflect Layers = Everything
            SerializedProperty reflectLayers = serialized.FindProperty("m_ReflectLayers");
            if (reflectLayers != null && reflectLayers.intValue == -1)
            {
                Issue issue = AddIssue(Category.MirrorTexture, Severity.Warning, go,
                    "Reflect Layers가 Everything — 월드 전체(파티클·UI 포함)를 반사하여 렌더링 비용이 두 배가 됩니다. 플레이어 전용 거울이라면 Player + MirrorReflection 레이어만 반사하세요.");

                int playerLayer = LayerMask.NameToLayer("Player");
                int mirrorLayer = LayerMask.NameToLayer("MirrorReflection");
                if (playerLayer >= 0 && mirrorLayer >= 0)
                {
                    int mask = (1 << playerLayer) | (1 << mirrorLayer);
                    issue.SetFix("레이어 제한",
                        () => ModifySerializedProperty(mirror, "m_ReflectLayers", p => p.intValue = mask),
                        $"거울 '{go.name}'의 Reflect Layers를 Player + MirrorReflection으로 제한합니다.\n월드 배경은 더 이상 반사되지 않습니다 (플레이어 전용 거울).\n\n계속하시겠습니까?");
                }
                else
                {
                    issue.Description += "\n(프로젝트에 VRChat 레이어가 없어 자동 수정할 수 없습니다.)";
                }
            }

            // (C) Disable Pixel Lights 꺼짐
            SerializedProperty disablePixelLights = serialized.FindProperty("m_DisablePixelLights");
            if (disablePixelLights != null
                && disablePixelLights.propertyType == SerializedPropertyType.Boolean
                && !disablePixelLights.boolValue)
            {
                Issue issue = AddIssue(Category.MirrorTexture, Severity.Warning, go,
                    "Disable Pixel Lights가 꺼져 있음 — 거울 반사 렌더링에도 실시간 픽셀 라이트가 계산되어 비용이 증가합니다.");
                issue.SetFix("픽셀라이트 끄기",
                    () => ModifySerializedProperty(mirror, "m_DisablePixelLights", p => p.boolValue = true));
            }
        }

        /// <summary>
        /// SDK 타입에 의존하지 않고 SerializedObject로 거울 설정을 수정합니다.
        /// ApplyModifiedProperties()가 Undo 기록과 프리팹 인스턴스 오버라이드를 Unity 네이티브 방식으로 처리합니다.
        /// </summary>
        private static bool ModifySerializedProperty(Object target, string propertyPath, Action<SerializedProperty> apply)
        {
            if (target == null)
                return false;

            var serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(propertyPath);
            if (property == null)
                return false;

            apply(property);
            serialized.ApplyModifiedProperties();
            MarkSceneDirty(target);
            return true;
        }

        // ---------------------------------------------------------------- 5-B. 고해상도 텍스처

        private void ScanHighResolutionTextures(List<GameObject> objects)
        {
            var usages = new Dictionary<Texture, AssetUsage>();
            var materialCache = new Dictionary<Material, List<Texture>>();
            var renderers = new List<Renderer>();
            var graphics = new List<Graphic>();

            foreach (GameObject go in objects)
            {
                go.GetComponents(renderers);
                foreach (Renderer renderer in renderers)
                {
                    if (renderer == null)
                        continue;
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        foreach (Texture texture in GetMaterialTextures(material, materialCache))
                            RegisterTextureUsage(usages, texture, go.name);
                    }
                }

                // UI Image/RawImage 텍스처 (Text/TMP의 폰트 Atlas는 제외)
                go.GetComponents(graphics);
                foreach (Graphic graphic in graphics)
                {
                    if (graphic == null || graphic is Text || graphic is TMP_Text)
                        continue;
                    RegisterTextureUsage(usages, graphic.mainTexture, go.name);
                }
            }

            // Lighting 설정의 Skybox (8K HDRI가 자주 문제됨)
            foreach (Texture texture in GetMaterialTextures(RenderSettings.skybox, materialCache))
                RegisterTextureUsage(usages, texture, "Skybox (Lighting 설정)");

            _metrics.UniqueMaterials = materialCache.Count;
            foreach (KeyValuePair<Texture, AssetUsage> pair in usages)
            {
                _metrics.TextureBytes += UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(pair.Key);
                CheckTextureSize(pair.Key, pair.Value);
            }
        }

        private static List<Texture> GetMaterialTextures(Material material, Dictionary<Material, List<Texture>> cache)
        {
            if (material == null)
                return s_NoTextures;
            if (cache.TryGetValue(material, out List<Texture> cached))
                return cached;

            var textures = new List<Texture>();
            var propertyIds = new List<int>();
            material.GetTexturePropertyNameIDs(propertyIds);
            foreach (int id in propertyIds)
            {
                // 셰이더에 없는 잔존 프로퍼티를 읽으면 콘솔 오류가 나므로 확인 후 접근
                if (!material.HasProperty(id))
                    continue;
                Texture texture = material.GetTexture(id);
                if (texture != null && !textures.Contains(texture))
                    textures.Add(texture);
            }

            cache[material] = textures;
            return textures;
        }

        private static void RegisterTextureUsage(Dictionary<Texture, AssetUsage> usages, Texture texture, string user)
        {
            if (texture == null)
                return;
            if (!usages.TryGetValue(texture, out AssetUsage usage))
            {
                usage = new AssetUsage { FirstUser = user };
                usages.Add(texture, usage);
            }
            usage.Count++;
        }

        private void CheckTextureSize(Texture texture, AssetUsage usage)
        {
            int maxDimension = Mathf.Max(texture.width, texture.height);
            if (maxDimension <= _textureMaxSize)
                return;

            string path = AssetDatabase.GetAssetPath(texture);
            TextureImporter importer = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as TextureImporter;
            bool isRenderTexture = texture is RenderTexture;

            // 폰트 Atlas·내장 리소스 등 TextureImporter가 없는 텍스처는 제외 (RenderTexture 에셋은 포함)
            if (importer == null && !isRenderTexture)
                return;

            long bytes = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(texture);
            float megabytes = ToMegabytes(bytes);
            var description = new StringBuilder();
            description.Append($"{texture.width}x{texture.height} 고해상도 텍스처 — 허용 크기 {_textureMaxSize}px 초과 (메모리 약 {megabytes:F1} MB)");
            description.Append($"\n사용처 {usage.Count}곳 (예: {usage.FirstUser}) · VRAM·다운로드 용량이 늘어 저사양 PC/Quest 환경의 성능이 저하됩니다.");

            Severity severity = maxDimension >= HugeTextureSize ? Severity.Error : Severity.Warning;
            Issue issue = AddIssue(Category.MirrorTexture, severity, texture, texture.name, path, description.ToString());
            issue.AssetPath = path;

            if (isRenderTexture)
            {
                issue.Description += "\nRenderTexture는 에셋 인스펙터에서 Size를 직접 줄이세요.";
                return;
            }
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
            {
                issue.Description += "\n패키지 내부 에셋이므로 자동 수정할 수 없습니다.";
                return;
            }

            int targetSize = _textureMaxSize;
            float ratio = (float)targetSize / maxDimension;
            issue.SavedBytes = (long)(bytes * (1f - ratio * ratio)); // 가로·세로가 같은 비율로 줄어든다고 보고 추정
            issue.SetFix($"{targetSize}로 제한", () => LimitTextureSize(path, targetSize),
                $"'{texture.name}' ({texture.width}x{texture.height})의 Max Size를 {targetSize}로 제한하고 다시 임포트합니다.\n(PC/Android/iOS 플랫폼 오버라이드 포함)\n\n" +
                "이 변경은 .meta 파일을 직접 수정하므로 Ctrl+Z로 되돌릴 수 없습니다. 원본 값이 백업되며, " +
                "[원본 크기로 복원 (Restore)] 버튼으로 되돌릴 수 있습니다.\n\n계속하시겠습니까?");
            issue.FixTooltip = "Max Size 축소 — Undo 대상이 아니며, 원본 값이 백업되어 [원본 크기로 복원 (Restore)]으로 되돌릴 수 있습니다.";
        }

        // ---------------------------------------------------------------- 3-B. 중복 지오메트리

        /// <summary>메시·머티리얼·월드 행렬이 모두 같은 활성 MeshRenderer를 묶어 완전히 겹친 중복을 찾음</summary>
        private void ScanDuplicateGeometry(List<GameObject> objects)
        {
            var groups = new Dictionary<string, List<MeshRenderer>>();
            var key = new StringBuilder(256);

            foreach (GameObject go in objects)
            {
                if (!go.activeInHierarchy)
                    continue;
                MeshRenderer renderer = go.GetComponent<MeshRenderer>();
                MeshFilter filter = go.GetComponent<MeshFilter>();
                if (renderer == null || !renderer.enabled || filter == null || filter.sharedMesh == null)
                    continue;

                // 그림자 전용 사본·거울 전용 레이어 사본처럼 의도적인 겹침은 키가 달라지도록 레이어와 그림자 모드를 포함
                key.Clear();
                key.Append(filter.sharedMesh.GetInstanceID());
                key.Append('|').Append(go.layer).Append('|').Append((int)renderer.shadowCastingMode);
                foreach (Material material in renderer.sharedMaterials)
                    key.Append('|').Append(material != null ? material.GetInstanceID() : 0);

                // 월드 행렬을 1mm 단위로 양자화해 위치·회전·스케일이 같은지 비교
                Matrix4x4 matrix = go.transform.localToWorldMatrix;
                for (int i = 0; i < 16; i++)
                    key.Append('|').Append(Mathf.RoundToInt(matrix[i] * 1000f));

                string groupKey = key.ToString();
                if (!groups.TryGetValue(groupKey, out List<MeshRenderer> group))
                {
                    group = new List<MeshRenderer>(2);
                    groups.Add(groupKey, group);
                }
                group.Add(renderer);
            }

            foreach (List<MeshRenderer> group in groups.Values)
            {
                if (group.Count < 2)
                    continue;

                // 다른 오브젝트가 참조 중인 쪽을 원본으로 남겨 참조가 끊기지 않게 함
                MeshRenderer keeper = group[0];
                foreach (MeshRenderer candidate in group)
                {
                    if (_externallyReferencedObjects.Contains(candidate.gameObject))
                    {
                        keeper = candidate;
                        break;
                    }
                }

                foreach (MeshRenderer duplicate in group)
                {
                    if (duplicate != keeper)
                        AddDuplicateIssue(duplicate, keeper, group.Count);
                }
            }
        }

        private void AddDuplicateIssue(MeshRenderer duplicate, MeshRenderer keeper, int groupSize)
        {
            GameObject go = duplicate.gameObject;
            GameObject keeperGo = keeper.gameObject;
            int drawCalls = duplicate.sharedMaterials.Length;

            Issue issue = AddIssue(Category.MeshColliderScale, Severity.Warning, go,
                $"'{keeperGo.name}'과(와) 메시·머티리얼·위치·회전·스케일이 완전히 같은 중복 오브젝트 ({groupSize}개 겹침) — " +
                $"같은 면이 두 번 그려져 Z-fighting(깜빡임)이 생기고 드로우콜 {drawCalls}개와 라이트맵 공간이 낭비됩니다.");
            issue.SavedDrawCalls = drawCalls;

            string blocker = GetDeleteBlocker(go, keeperGo);
            if (blocker != null)
            {
                issue.Description += $"\n{blocker} — 자동 삭제하지 않습니다. 확인 후 직접 정리하세요.";
                return;
            }

            issue.SetFix("중복 삭제", () =>
            {
                if (go == null)
                    return false;
                Scene scene = go.scene;
                Undo.DestroyObjectImmediate(go);
                if (scene.IsValid() && scene.isLoaded)
                    EditorSceneManager.MarkSceneDirty(scene);
                return true;
            }, $"중복 오브젝트 '{go.name}'을(를) 삭제하고 '{keeperGo.name}'만 남깁니다.\nCtrl+Z로 되돌릴 수 있습니다.\n\n계속하시겠습니까?");
        }

        /// <summary>중복 오브젝트를 자동 삭제하면 안 되는 이유 (안전하면 null)</summary>
        private string GetDeleteBlocker(GameObject go, GameObject keeper)
        {
            if (go.transform.childCount > 0)
                return "하위 오브젝트가 있음";
            if (_externallyReferencedObjects.Contains(go))
                return "Udon·UI 이벤트 등 다른 컴포넌트가 이 오브젝트를 참조 중";
            if (PrefabUtility.IsPartOfPrefabInstance(go) && !PrefabUtility.IsOutermostPrefabInstanceRoot(go))
                return "프리팹 인스턴스 내부 오브젝트";
            for (Transform t = go.transform; t != null; t = t.parent)
            {
                if (t.GetComponent<Animator>() != null)
                    return "Animator 하위 오브젝트(애니메이션 경로가 끊길 수 있음)";
            }

            go.GetComponents(s_ComponentBuffer);
            foreach (Component component in s_ComponentBuffer)
            {
                if (component == null)
                    return "Missing Script가 있음";
                if (!(component is Transform || component is MeshFilter || component is MeshRenderer || component is Collider))
                    return $"추가 컴포넌트({component.GetType().Name})가 있음";
                if (component is Collider && keeper.GetComponent(component.GetType()) == null)
                    return $"원본에 없는 콜라이더({component.GetType().Name})가 있음 — 삭제하면 충돌 판정이 사라짐";
            }
            return null;
        }

        // ---------------------------------------------------------------- 6. 참조 무결성

        private void ScanReferenceIntegrity(List<GameObject> objects, float progressStart, float progressEnd)
        {
            var components = new List<Component>();
            var missingPrefabRoots = new HashSet<GameObject>();
            var brokenMaterials = new Dictionary<Material, AssetUsage>();
            var shaderChecked = new Dictionary<Material, bool>();
            var missing = new List<string>();

            for (int i = 0; i < objects.Count; i++)
            {
                if (i % 200 == 0)
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, $"6. 참조 무결성 검사 중... ({i:N0}/{objects.Count:N0})",
                        Mathf.Lerp(progressStart, progressEnd, (float)i / objects.Count));
                }

                GameObject go = objects[i];

                // (A) 원본 에셋이 삭제·이동된 프리팹 인스턴스 (Missing Prefab)
                if (PrefabUtility.GetPrefabInstanceStatus(go) == PrefabInstanceStatus.MissingAsset)
                {
                    GameObject root = PrefabUtility.GetNearestPrefabInstanceRoot(go);
                    if (root == null)
                        root = go;
                    if (missingPrefabRoots.Add(root))
                    {
                        AddIssue(Category.References, Severity.Error, root,
                            "원본 프리팹 에셋이 삭제·이동된 Missing Prefab — 프리팹 내용이 월드에서 사라지거나 빌드 오류가 날 수 있습니다. 원본 에셋을 복구하거나, 필요 없다면 오브젝트를 삭제하세요.");
                    }
                }

                // (B) 컴포넌트 직렬화 필드: Missing 참조 / 빈 머티리얼 슬롯 / 프로그램 없는 UdonBehaviour
                missing.Clear();
                int emptyMaterialSlots = 0;
                bool emptyUdonProgram = false;
                go.GetComponents(components);
                foreach (Component component in components)
                {
                    if (component == null || component is Transform)
                        continue;
                    InspectSerializedReferences(component, missing, ref emptyMaterialSlots, ref emptyUdonProgram);

                    // (C) 셰이더 누락·컴파일 오류 머티리얼 (분홍색 렌더링)
                    Renderer renderer = component as Renderer;
                    if (renderer is MeshRenderer || renderer is SkinnedMeshRenderer)
                        CollectBrokenShaderMaterials(renderer, shaderChecked, brokenMaterials);
                }

                if (missing.Count > 0)
                {
                    var description = new StringBuilder();
                    description.Append($"Missing 참조 {missing.Count}개 — 삭제·이동된 에셋이나 오브젝트를 가리키고 있어 런타임에 null이 됩니다 (Udon NullReference로 스크립트가 멈추는 주요 원인).\n");
                    const int maxListed = 4;
                    description.Append(string.Join(", ", missing.GetRange(0, Mathf.Min(maxListed, missing.Count))));
                    if (missing.Count > maxListed)
                        description.Append($" 외 {missing.Count - maxListed}개");
                    AddIssue(Category.References, Severity.Error, go, description.ToString());
                }

                if (emptyMaterialSlots > 0 && HasRenderableMesh(go))
                {
                    AddIssue(Category.References, Severity.Error, go,
                        $"머티리얼 슬롯 {emptyMaterialSlots}개가 비어 있음(None) — 해당 서브메시가 분홍색(에러 셰이더)으로 렌더링됩니다. 머티리얼을 지정하거나 슬롯 수를 메시에 맞추세요.");
                }

                if (emptyUdonProgram)
                {
                    AddIssue(Category.References, Severity.Error, go,
                        "UdonBehaviour에 Program Source가 지정되지 않음 — 아무 동작도 하지 않습니다. 의도한 Udon/U# 프로그램을 지정하거나 컴포넌트를 제거하세요.");
                }
            }

            foreach (KeyValuePair<Material, AssetUsage> pair in brokenMaterials)
            {
                Material material = pair.Key;
                string shaderName = material.shader != null ? material.shader.name : "None";
                AddIssue(Category.References, Severity.Error, material, material.name, AssetDatabase.GetAssetPath(material),
                    $"셰이더를 찾을 수 없음('{shaderName}') — 이 머티리얼을 쓰는 렌더러 {pair.Value.Count}곳(예: {pair.Value.FirstUser})이 분홍색으로 렌더링됩니다.\n" +
                    "셰이더 패키지(lilToon, Poiyomi 등)가 프로젝트에 설치되어 있는지 확인하세요.");
            }
        }

        /// <summary>
        /// 컴포넌트의 모든 직렬화 필드를 순회해 Missing 참조를 찾고,
        /// 정상 참조는 '다른 오브젝트가 참조 중' 색인에 기록합니다.
        /// </summary>
        private void InspectSerializedReferences(Component component, List<string> missing, ref int emptyMaterialSlots, ref bool emptyUdonProgram)
        {
            bool isMeshRenderer = component is MeshRenderer || component is SkinnedMeshRenderer;
            string typeName = component.GetType().Name;
            GameObject owner = component.gameObject;

            using (var serialized = new SerializedObject(component))
            {
                SerializedProperty property = serialized.GetIterator();
                bool enterChildren = true;
                while (property.Next(enterChildren))
                {
                    enterChildren = ShouldEnterChildren(property);
                    if (property.propertyType != SerializedPropertyType.ObjectReference || k_IgnoredReferenceProperties.Contains(property.name))
                        continue;

                    Object value = property.objectReferenceValue;
                    if (value != null)
                    {
                        RegisterExternalReference(owner, value);
                        continue;
                    }

                    // 값이 null인데 Instance ID가 남아 있으면 'Missing'(삭제된 대상), 0이면 단순 'None'
                    if (property.objectReferenceInstanceIDValue != 0)
                        missing.Add($"{typeName}.{FormatPropertyPath(property.propertyPath)}");
                    else if (isMeshRenderer && property.propertyPath.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal))
                        emptyMaterialSlots++;
                }

                // UdonBehaviour는 SDK 타입에 의존하지 않도록 이름과 직렬화 필드로 판별
                if (typeName == "UdonBehaviour"
                    && IsNoneReference(serialized.FindProperty("programSource"))
                    && IsNoneReference(serialized.FindProperty("serializedProgramAsset")))
                    emptyUdonProgram = true;
            }
        }

        private static bool HasRenderableMesh(GameObject go)
        {
            MeshFilter meshFilter = go.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
                return true;
            SkinnedMeshRenderer skinned = go.GetComponent<SkinnedMeshRenderer>();
            return skinned != null && skinned.sharedMesh != null;
        }

        private void RegisterExternalReference(GameObject owner, Object value)
        {
            GameObject target = value as GameObject;
            if (target == null)
            {
                Component component = value as Component;
                if (component == null)
                    return; // 에셋 참조(메시·머티리얼 등)는 삭제 대상 판단과 무관
                target = component.gameObject;
            }
            if (target != owner)
                _externallyReferencedObjects.Add(target);
        }

        private static bool ShouldEnterChildren(SerializedProperty property)
        {
            if (!property.hasChildren)
                return false;
            if (property.propertyType != SerializedPropertyType.Generic && property.propertyType != SerializedPropertyType.ManagedReference)
                return false;
            return !property.isArray || !k_PrimitiveArrayElementTypes.Contains(property.arrayElementType);
        }

        private static bool IsNoneReference(SerializedProperty property)
        {
            return property != null
                && property.propertyType == SerializedPropertyType.ObjectReference
                && property.objectReferenceValue == null
                && property.objectReferenceInstanceIDValue == 0;
        }

        /// <summary>"m_Materials.Array.data[1]" → "m_Materials[1]" + 출처 표시</summary>
        private static string FormatPropertyPath(string propertyPath)
        {
            string path = propertyPath.Replace(".Array.data[", "[");
            if (propertyPath.Contains("publicVariablesUnityEngineObjects"))
                return path + " (Udon 공개 변수)";
            if (propertyPath.Contains("m_PersistentCalls"))
                return path + " (UnityEvent 대상)";
            return path;
        }

        private static void CollectBrokenShaderMaterials(Renderer renderer, Dictionary<Material, bool> checkedMaterials,
            Dictionary<Material, AssetUsage> brokenMaterials)
        {
            foreach (Material material in renderer.sharedMaterials)
            {
                if (material == null)
                    continue;
                if (!checkedMaterials.TryGetValue(material, out bool broken))
                {
                    Shader shader = material.shader;
                    // 셰이더 파일이 없거나 불러오지 못하면 Unity가 InternalErrorShader로 대체함 (플랫폼별 컴파일 경고로 인한 오탐 방지)
                    broken = shader == null || shader.name == ErrorShaderName;
                    checkedMaterials.Add(material, broken);
                }
                if (!broken)
                    continue;

                if (!brokenMaterials.TryGetValue(material, out AssetUsage usage))
                {
                    usage = new AssetUsage { FirstUser = renderer.gameObject.name };
                    brokenMaterials.Add(material, usage);
                }
                usage.Count++;
            }
        }

        // ---------------------------------------------------------------- 월드 지표

        private void CollectWorldMetrics(List<GameObject> objects)
        {
            foreach (GameObject go in objects)
            {
                if (!go.activeInHierarchy)
                    continue;

                MeshRenderer meshRenderer = go.GetComponent<MeshRenderer>();
                MeshFilter meshFilter = go.GetComponent<MeshFilter>();
                if (meshRenderer != null && meshRenderer.enabled && meshFilter != null && meshFilter.sharedMesh != null)
                    _metrics.Triangles += GetTriangleCount(meshFilter.sharedMesh);

                SkinnedMeshRenderer skinned = go.GetComponent<SkinnedMeshRenderer>();
                if (skinned != null && skinned.enabled && skinned.sharedMesh != null)
                    _metrics.Triangles += GetTriangleCount(skinned.sharedMesh);

                Light light = go.GetComponent<Light>();
                if (light != null && light.enabled && light.lightmapBakeType != LightmapBakeType.Baked)
                    _metrics.RealtimeLights++;
            }
        }

        #endregion

        // ====================================================================
        #region 자동 수정 (Fix)
        // ====================================================================

        private void ApplySingleFix(Issue issue)
        {
            if (!issue.CanFix || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            if (issue.FixConfirm != null && !EditorUtility.DisplayDialog("자동 수정 확인", issue.FixConfirm, "수정", "취소"))
                return;

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName($"[Pre-Flight] {issue.FixLabel}: {issue.ObjectName}");

            ExecuteFix(issue);

            Undo.CollapseUndoOperations(undoGroup);
        }

        private void BulkFix(Category category)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            int index = (int)category;
            List<Issue> targets = _issues[index].FindAll(issue => issue.CanFix);
            if (targets.Count == 0)
                return;

            int visualChanges = targets.FindAll(issue => issue.FixConfirm != null).Count;
            var message = new StringBuilder($"'{k_CategoryTitles[index]}' 카테고리의 {targets.Count}개 항목을 자동 수정합니다.");
            if (visualChanges > 0)
                message.Append($"\n\n주의: 이 중 {visualChanges}개는 외형이나 동작이 바뀔 수 있는 수정입니다. (개별 확인 생략)");
            bool hasTextureFixes = targets.Exists(issue => issue.AssetPath != null);
            if (hasTextureFixes)
            {
                message.Append("\n\n텍스처 Max Size 축소는 .meta 파일을 직접 수정하므로 Ctrl+Z로 되돌릴 수 없습니다. ");
                message.Append("원본 값이 백업되며 [원본 크기로 복원 (Restore)] 버튼으로 되돌릴 수 있습니다. 재임포트에 시간이 걸릴 수 있습니다.");
                message.Append("\n\n텍스처를 제외한 변경은 하나의 Undo 단계로 기록됩니다.");
            }
            else
            {
                message.Append("\n\n모든 변경은 하나의 Undo 단계로 기록되어 Ctrl+Z 한 번으로 되돌릴 수 있습니다.");
            }

            if (!EditorUtility.DisplayDialog("일괄 자동 수정", message.ToString(), "수정 실행", "취소"))
                return;

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName($"[Pre-Flight] {k_CategoryTitles[index]} 일괄 수정");

            int succeeded = 0;
            AssetDatabase.StartAssetEditing(); // 텍스처 재임포트를 모아서 한 번에 처리
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    EditorUtility.DisplayProgressBar("일괄 자동 수정", targets[i].ObjectName, (float)i / targets.Count);
                    if (ExecuteFix(targets[i]))
                        succeeded++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
            }

            Undo.CollapseUndoOperations(undoGroup);
            Debug.Log($"{LogPrefix}{k_CategoryTitles[index]}: {targets.Count}개 중 {succeeded}개 항목을 자동 수정했습니다.");
        }

        private static bool ExecuteFix(Issue issue)
        {
            if (!issue.CanFix)
                return false;
            try
            {
                issue.IsFixed = issue.FixAction();
            }
            catch (Exception e)
            {
                issue.IsFixed = false;
                Debug.LogError($"{LogPrefix}'{issue.ObjectName}' 자동 수정 실패: {e.Message}", issue.Target);
                Debug.LogException(e);
            }
            return issue.IsFixed;
        }

        /// <summary>
        /// 씬 오브젝트를 직접 수정한 뒤의 공통 처리: 프리팹 인스턴스 오버라이드 기록 + 씬 Dirty 표시.
        /// (Undo 기록은 각 수정 함수에서 값을 바꾸기 직전에 수행)
        /// </summary>
        private static void MarkModified(Object modified)
        {
            if (modified == null)
                return;

            if (PrefabUtility.IsPartOfPrefabInstance(modified))
                PrefabUtility.RecordPrefabInstancePropertyModifications(modified);

            MarkSceneDirty(modified);
        }

        private static void MarkSceneDirty(Object modified)
        {
            if (modified == null)
                return;

            GameObject go = modified as GameObject;
            if (go == null)
            {
                Component component = modified as Component;
                if (component != null)
                    go = component.gameObject;
            }

            if (go != null && go.scene.IsValid() && go.scene.isLoaded)
                EditorSceneManager.MarkSceneDirty(go.scene);
        }

        // ---------------------------------------------------------------- 텍스처 Max Size 축소 + 원본 백업/복원

        /// <summary>
        /// 텍스처 Max Size를 제한하고 재임포트합니다.
        /// SaveAndReimport()는 디스크의 .meta를 직접 바꾸므로 Undo로 관리하지 않습니다.
        /// 대신 최초 원본 값을 세션 백업에 보관했다가 [원본 크기로 복원 (Restore)]으로 되돌립니다.
        /// </summary>
        private static bool LimitTextureSize(string path, int maxSize)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
                return false;

            LoadTextureBackups();

            // 여러 번 축소해도 최초 원본 값이 남도록 이미 백업된 값은 덮어쓰지 않음
            bool addedDefaultBackup = false;
            if (!s_OriginalMaxTextureSizes.ContainsKey(path))
            {
                s_OriginalMaxTextureSizes.Add(path, importer.maxTextureSize);
                addedDefaultBackup = true;
            }

            bool changed = false;
            if (importer.maxTextureSize > maxSize)
            {
                importer.maxTextureSize = maxSize;
                changed = true;
            }

            foreach (string platform in k_TexturePlatforms)
            {
                TextureImporterPlatformSettings settings = importer.GetPlatformTextureSettings(platform);
                if (!settings.overridden || settings.maxTextureSize <= maxSize)
                    continue;

                string key = PlatformBackupKey(path, platform);
                if (!s_OriginalPlatformMaxSizes.ContainsKey(key))
                    s_OriginalPlatformMaxSizes.Add(key, settings.maxTextureSize);
                settings.maxTextureSize = maxSize;
                importer.SetPlatformTextureSettings(settings);
                changed = true;
            }

            Object asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (!changed)
            {
                if (addedDefaultBackup)
                    s_OriginalMaxTextureSizes.Remove(path);
                Debug.LogWarning($"{LogPrefix}'{path}'의 Max Size가 이미 {maxSize} 이하입니다. 다른 플랫폼 설정이나 텍스처 형식을 확인하세요.", asset);
                return false;
            }

            SaveTextureBackups();
            importer.SaveAndReimport();
            Debug.Log($"{LogPrefix}'{path}' Max Size를 {maxSize}로 제한했습니다. (원본: {DescribeTextureBackup(path)}) — [원본 크기로 복원 (Restore)]으로 되돌릴 수 있습니다.", asset);
            return true;
        }

        /// <summary>백업해 둔 원본 Max Size(기본 + 플랫폼 오버라이드)로 되돌리고 재임포트</summary>
        private static bool RestoreTextureSize(string path)
        {
            LoadTextureBackups();
            if (!s_OriginalMaxTextureSizes.TryGetValue(path, out int originalSize))
                return false;

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                Debug.LogWarning($"{LogPrefix}'{path}' 텍스처를 찾을 수 없어(이동 또는 삭제됨) 백업을 삭제합니다.");
                RemoveTextureBackup(path);
                return false;
            }

            importer.maxTextureSize = originalSize;
            foreach (string platform in k_TexturePlatforms)
            {
                if (!s_OriginalPlatformMaxSizes.TryGetValue(PlatformBackupKey(path, platform), out int platformSize))
                    continue;
                TextureImporterPlatformSettings settings = importer.GetPlatformTextureSettings(platform);
                settings.maxTextureSize = platformSize;
                importer.SetPlatformTextureSettings(settings);
            }

            string restored = DescribeTextureBackup(path);
            RemoveTextureBackup(path);
            importer.SaveAndReimport();
            Debug.Log($"{LogPrefix}'{path}' Max Size를 원본({restored})으로 복원했습니다.", AssetDatabase.LoadMainAssetAtPath(path));
            return true;
        }

        private void RestoreTexture(string path)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            if (!EditorUtility.DisplayDialog("원본 크기로 복원",
                    $"'{path}'의 Max Size를 원본 값({DescribeTextureBackup(path)})으로 되돌리고 다시 임포트합니다.\n\n계속하시겠습니까?",
                    "복원", "취소"))
                return;

            if (RestoreTextureSize(path))
                ReopenTextureIssues(path);
        }

        private void RestoreAllTextures()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            LoadTextureBackups();
            var paths = new List<string>(s_OriginalMaxTextureSizes.Keys);
            if (paths.Count == 0)
                return;
            if (!EditorUtility.DisplayDialog("원본 크기로 복원",
                    $"백업된 텍스처 {paths.Count}개의 Max Size를 모두 원본 값으로 되돌리고 다시 임포트합니다.\n\n계속하시겠습니까?",
                    "모두 복원", "취소"))
                return;

            AssetDatabase.StartAssetEditing(); // 재임포트를 모아서 한 번에 처리
            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    EditorUtility.DisplayProgressBar("원본 크기로 복원", paths[i], (float)i / paths.Count);
                    if (RestoreTextureSize(paths[i]))
                        ReopenTextureIssues(paths[i]);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>복원된 텍스처의 스캔 결과를 다시 미해결 상태로 되돌려 Fix를 재사용할 수 있게 함</summary>
        private void ReopenTextureIssues(string path)
        {
            foreach (Issue issue in _issues[(int)Category.MirrorTexture])
            {
                if (issue.AssetPath == path)
                    issue.IsFixed = false;
            }
        }

        private static string PlatformBackupKey(string path, string platform)
        {
            return path + "|" + platform;
        }

        /// <summary>백업된 원본 값을 "Default 4096, Android 4096" 형태로 요약</summary>
        private static string DescribeTextureBackup(string path)
        {
            var builder = new StringBuilder();
            if (s_OriginalMaxTextureSizes.TryGetValue(path, out int originalSize))
                builder.Append("Default ").Append(originalSize);
            foreach (string platform in k_TexturePlatforms)
            {
                if (s_OriginalPlatformMaxSizes.TryGetValue(PlatformBackupKey(path, platform), out int platformSize))
                    builder.Append(", ").Append(platform).Append(' ').Append(platformSize);
            }
            return builder.ToString();
        }

        private static void RemoveTextureBackup(string path)
        {
            s_OriginalMaxTextureSizes.Remove(path);
            foreach (string platform in k_TexturePlatforms)
                s_OriginalPlatformMaxSizes.Remove(PlatformBackupKey(path, platform));
            SaveTextureBackups();
        }

        // 스크립트 리컴파일(도메인 리로드)에도 백업이 사라지지 않도록 SessionState에 보관 — 에디터를 닫으면 삭제됨
        private static void LoadTextureBackups()
        {
            if (s_TextureBackupsLoaded)
                return;
            s_TextureBackupsLoaded = true;
            DeserializeBackup(SessionState.GetString(DefaultSizeBackupKey, string.Empty), s_OriginalMaxTextureSizes);
            DeserializeBackup(SessionState.GetString(PlatformSizeBackupKey, string.Empty), s_OriginalPlatformMaxSizes);
        }

        private static void SaveTextureBackups()
        {
            SessionState.SetString(DefaultSizeBackupKey, SerializeBackup(s_OriginalMaxTextureSizes));
            SessionState.SetString(PlatformSizeBackupKey, SerializeBackup(s_OriginalPlatformMaxSizes));
        }

        private static string SerializeBackup(Dictionary<string, int> backup)
        {
            var builder = new StringBuilder();
            foreach (KeyValuePair<string, int> pair in backup)
                builder.Append(pair.Key).Append('\t').Append(pair.Value).Append('\n');
            return builder.ToString();
        }

        private static void DeserializeBackup(string data, Dictionary<string, int> backup)
        {
            backup.Clear();
            foreach (string line in data.Split('\n'))
            {
                int tab = line.LastIndexOf('\t');
                if (tab <= 0)
                    continue;
                if (int.TryParse(line.Substring(tab + 1), out int size))
                    backup[line.Substring(0, tab)] = size;
            }
        }

        #endregion

        // ====================================================================
        #region 리포트 (Markdown)
        // ====================================================================

        private void CopyReport()
        {
            EditorGUIUtility.systemCopyBuffer = BuildMarkdownReport();
            ShowNotification(new GUIContent("리포트를 클립보드에 복사했습니다."));
        }

        private void SaveReport()
        {
            string path = EditorUtility.SaveFilePanel("Pre-Flight 리포트 저장", string.Empty,
                $"VRC-PreFlight-{DateTime.Now:yyyyMMdd-HHmm}.md", "md");
            if (string.IsNullOrEmpty(path))
                return;

            System.IO.File.WriteAllText(path, BuildMarkdownReport(), new UTF8Encoding(false));
            Debug.Log($"{LogPrefix}리포트를 저장했습니다: {path}");
        }

        /// <summary>스캔 결과·월드 지표·이전 스캔 대비 변화를 이슈/PR에 붙여 넣을 수 있는 Markdown으로 정리</summary>
        private string BuildMarkdownReport()
        {
            CountIssues(out int errors, out int warnings, out int fixedCount);
            var report = new StringBuilder();
            report.AppendLine("# VRC World Pre-Flight Report");
            report.AppendLine();
            report.AppendLine($"- 생성: {DateTime.Now:yyyy-MM-dd HH:mm} (마지막 스캔 {_lastScanTime})");
            report.AppendLine($"- 씬: {string.Join(", ", GetLoadedSceneNames())}");
            report.AppendLine($"- 검사 오브젝트: {_scannedObjectCount:N0}개");
            report.AppendLine($"- 결과: 오류 {errors} · 경고 {warnings} · 자동 수정 {fixedCount}");

            if (_metrics != null)
            {
                report.AppendLine();
                report.AppendLine("## 월드 지표");
                report.AppendLine();
                report.AppendLine(_previousMetrics != null ? "| 항목 | 이전 스캔 | 현재 |" : "| 항목 | 현재 |");
                report.AppendLine(_previousMetrics != null ? "| --- | ---: | ---: |" : "| --- | ---: |");
                AppendMetricRow(report, "오류", m => m.Errors.ToString("N0"));
                AppendMetricRow(report, "경고", m => m.Warnings.ToString("N0"));
                AppendMetricRow(report, "삼각형", m => m.Triangles.ToString("N0"));
                AppendMetricRow(report, "머티리얼", m => m.UniqueMaterials.ToString("N0"));
                AppendMetricRow(report, "텍스처 메모리 (MB, 추정)", m => ToMegabytes(m.TextureBytes).ToString("F1"));
                AppendMetricRow(report, "실시간/혼합 라이트", m => m.RealtimeLights.ToString("N0"));
            }

            for (int i = 0; i < CategoryCount; i++)
            {
                List<Issue> list = _issues[i];
                int categoryErrors = 0, categoryWarnings = 0, categoryFixed = 0;
                CountList(list, ref categoryErrors, ref categoryWarnings, ref categoryFixed);

                report.AppendLine();
                report.AppendLine($"## {k_CategoryTitles[i]} — 오류 {categoryErrors} · 경고 {categoryWarnings} · 수정 {categoryFixed}");
                report.AppendLine();
                if (list.Count == 0)
                {
                    report.AppendLine("문제 없음");
                    continue;
                }

                report.AppendLine("| 심각도 | 대상 | 내용 | 상태 |");
                report.AppendLine("| --- | --- | --- | --- |");
                foreach (Issue issue in list)
                {
                    string state = issue.IsFixed ? "수정됨" : issue.IsAlive ? "미해결" : "대상 없음";
                    string target = string.IsNullOrEmpty(issue.Tooltip) ? issue.ObjectName : issue.Tooltip;
                    report.AppendLine($"| {(issue.Severity == Severity.Error ? "오류" : "경고")} | `{EscapeTableCell(target)}` | {EscapeTableCell(issue.Description)} | {state} |");
                }
            }
            return report.ToString();
        }

        private void AppendMetricRow(StringBuilder report, string label, Func<WorldMetrics, string> value)
        {
            report.AppendLine(_previousMetrics != null
                ? $"| {label} | {value(_previousMetrics)} | {value(_metrics)} |"
                : $"| {label} | {value(_metrics)} |");
        }

        private static string EscapeTableCell(string text)
        {
            return text.Replace("|", "\\|").Replace("\r", string.Empty).Replace("\n", "<br>");
        }

        private static List<string> GetLoadedSceneNames()
        {
            var names = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                    names.Add(string.IsNullOrEmpty(scene.name) ? "(Untitled)" : scene.name);
            }
            return names;
        }

        #endregion

        // ====================================================================
        #region 유틸리티
        // ====================================================================

        private static string GetHierarchyPath(GameObject go)
        {
            var builder = new StringBuilder(go.name);
            for (Transform parent = go.transform.parent; parent != null; parent = parent.parent)
                builder.Insert(0, '/').Insert(0, parent.name);
            return $"[{go.scene.name}] {builder}";
        }

        private static string FormatVector(Vector3 v)
        {
            return $"({v.x:0.##}, {v.y:0.##}, {v.z:0.##})";
        }

        #endregion
    }
}
