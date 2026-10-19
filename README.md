# VRC-World-Checker
VRChat 3D 월드 배포 전, 버튼 하나로 프로젝트의 무결성과 최적화 상태를 진단하고 자동 수정(Auto-Fix)할 수 있는 Unity Custom EditorWindow 스크립트입니다.

## 설치

1. `Assets/Editor/VRCWorldPreFlightChecker.cs` 파일을 VRChat 월드 프로젝트의 `Assets/Editor/` 폴더에 복사합니다.
2. Unity 메뉴 **Tools > VRC World Pre-Flight Checker** 를 엽니다.

- 대상 환경: Unity 2022.3 LTS + VRChat SDK3 Worlds
- 의존성: `UnityEditor`, `UnityEngine`, `UnityEngine.UI`, `TMPro` 만 사용 (VRChat SDK에 컴파일 의존성 없음 — 거울 컴포넌트는 타입 이름으로 식별)

## 사용법

1. **[전체 월드 스캔 실행 (Run Full Scan)]** 버튼을 누르면 로드된 모든 씬의 GameObject(비활성 포함)를 검사합니다.
2. 상단 대시보드에서 오류/경고/수정 완료 개수를 확인합니다.
3. 카테고리별 Foldout에서 각 항목의 **[Select]** (선택 및 Ping) 또는 **[Fix]** (자동 수정) 버튼을 사용합니다.
   카테고리 헤더의 **[일괄 제거]/[일괄 수정]** 버튼으로 한 번에 수정할 수도 있습니다.
4. 수정 후 하단의 **[씬·에셋 저장]** 으로 씬과 수정된 에셋(TMP 머티리얼 등)을 저장합니다.

씬 수정은 Undo 그룹으로 기록되므로 **Ctrl+Z** 로 되돌릴 수 있습니다 (일괄 수정은 한 번의 Ctrl+Z로 전체 취소).
단, 텍스처 Max Size 축소는 `.meta` 파일을 직접 바꾸므로 Undo 대상이 아니며, **[원본 크기로 복원 (Restore)]** 버튼으로 되돌립니다 (아래 참고).

## 검사 항목

| 카테고리 | 검사 내용 | 심각도 | 자동 수정 |
| --- | --- | --- | --- |
| 1. Missing Scripts | `GetMonoBehavioursWithMissingScriptCount > 0` 인 오브젝트 | 오류 | 제거 (`RegisterCompleteObjectUndo` → `RemoveMonoBehavioursWithMissingScript`). 원본 프리팹에 포함된 Missing Script는 프리팹 에셋에서 직접 제거하도록 안내 |
| 2. Animator & Static 충돌 | Animator 루트 본체가 Static인 경우, 그리고 클립의 모든 바인딩(`GetCurveBindings` + `GetObjectReferenceCurveBindings`)이 가리키는 경로의 Static 오브젝트 — Transform 이동, `m_IsActive` 토글, 머티리얼/컴포넌트 프로퍼티 모두 포함. Transform 이동과 `m_IsActive` 토글은 하위 자식에게도 전파되는 것으로 판정 | Batching Static + 이동/루트, Occluder Static + `m_IsActive` 토글이면 오류, 그 외 경고 | Static 해제 (`go.isStatic = false`) |
| 3. MeshCollider & Negative Scale | `lossyScale` 음수(메시 렌더러/콜라이더 보유 시) | 경고 | 스케일 양수화 (확인 대화상자) |
| | `MeshRenderer.bounds` 중심이 피벗에서 비정상적으로 먼 경우 (임계값 초과 + 피벗이 Bounds 구 바깥) | 경고 | 수동 (모델링 툴에서 피벗 재설정) |
| | 메시가 없는 MeshCollider | 오류 | MeshFilter 메시 할당 |
| | 고폴리곤 Non-Convex MeshCollider | 경고 | 수동 |
| 4. Legacy UI Text & 깨진 TMP | `UnityEngine.UI.Text` 사용 (Dynamic Font 4096 텍스처 초과 위험) | 경고 | 수동 (Udon 참조가 끊길 수 있어 자동 변환하지 않음) |
| | `TMP_Text` 폰트 없음 / `fontSharedMaterial.mainTexture == null` (검은 사각형 버그) | 오류 | 폰트 없음 → 기본 폰트 지정. Atlas 누락 → ① 현재 머티리얼 `_MainTex`에 `font.atlasTexture` 재연결 (Outline/Underlay 등 프리셋 유지) ② 불가 시 폰트 기본 머티리얼로 교체 ③ 기본 머티리얼도 Atlas를 잃었으면 기본 머티리얼에 재연결 |
| 5. 거울 & 고해상도 텍스처 | 기본 활성화된 거울 (`VRCMirrorReflection` / `VRC_MirrorReflection`) | 경고 | 기본 비활성화 (확인 대화상자) |
| | `m_ReflectLayers` = Everything | 경고 | Player + MirrorReflection 으로 제한 (확인 대화상자, `SerializedObject.ApplyModifiedProperties`) |
| | `m_DisablePixelLights` 꺼짐 | 경고 | 켜기 (`SerializedObject.ApplyModifiedProperties`) |
| | 씬에서 사용 중인 텍스처가 허용 크기 초과 (8192 이상은 오류) | 경고/오류 | 확인 대화상자 후 Max Size 제한 + 재임포트 (PC/Android/iOS 오버라이드 포함), 원본 값 백업 |

## 검사 설정

창의 **검사 설정 (임계값)** Foldout에서 변경하며 EditorPrefs에 저장됩니다.

- Bounds 오프셋 임계값 (기본 20m)
- MeshCollider 삼각형 경고 기준 (기본 20,000)
- 텍스처 최대 허용 크기 (기본 2048px)
- EditorOnly 태그 오브젝트 제외 (기본 켜짐 — 빌드에서 제거되므로)

## 텍스처 원본 크기 복원

`TextureImporter.SaveAndReimport()`는 디스크의 `.meta` 파일을 직접 바꾸기 때문에, Undo 시스템에 묶으면 일반 씬 작업 중 Ctrl+Z가 예기치 않은 재임포트나 에디터 멈춤을 일으킬 수 있습니다. 그래서 텍스처 축소는 Undo로 관리하지 않습니다.

- 축소 전에 확인 대화상자를 띄우고, 원본 값을 `Dictionary<string, int>` (에셋 경로 → 기존 `maxTextureSize`)에 백업합니다. 플랫폼 오버라이드 값은 별도 딕셔너리(`"경로|플랫폼"` → 값)에 백업합니다.
- 같은 텍스처를 여러 번 축소해도 **최초 원본 값**이 유지됩니다.
- 백업은 `SessionState`에도 저장되므로 스크립트 리컴파일(도메인 리로드) 후에도 남아 있고, 에디터를 닫으면 사라집니다.
- 카테고리 5를 펼치면 **축소한 텍스처 원본 백업** 목록이 표시되며, 항목별 **[원본 크기로 복원 (Restore)]** 또는 **[모두 복원]** 으로 되돌릴 수 있습니다. 복원 결과와 원본 값은 콘솔 로그에도 남습니다.

## 참고 사항

- 씬 오브젝트 수정은 `Undo.RecordObject`(Missing Script는 `Undo.RegisterCompleteObjectUndo`) 후 `EditorSceneManager.MarkSceneDirty`를 호출합니다.
- 거울 설정은 VRChat SDK 타입이나 C# 리플렉션 없이 `new SerializedObject(mirror)` + `FindProperty("m_ReflectLayers" / "m_DisablePixelLights")`로 읽고, `ApplyModifiedProperties()`로 수정합니다 (Undo·프리팹 오버라이드를 Unity가 직접 처리).
- TMP Atlas 재연결은 공유 머티리얼 에셋을 수정합니다 (`Undo.RecordObject`로 기록되어 Ctrl+Z 가능). 머티리얼에 기록된 `_TextureWidth`/`_TextureHeight`가 폰트 Atlas 크기와 다르면 다른 폰트용 프리셋으로 보고 재연결하지 않습니다.
- 플레이 모드에서는 스캔과 자동 수정이 비활성화됩니다.
- Undo/Redo, 씬 열기/닫기, 설정 변경 후에는 결과가 오래되었다는 안내가 표시되며 [다시 스캔]으로 갱신할 수 있습니다.
