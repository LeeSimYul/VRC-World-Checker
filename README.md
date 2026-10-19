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
4. 수정 후 하단의 **[열린 씬 저장]** 으로 씬을 저장합니다.

모든 자동 수정은 Undo 그룹으로 기록되므로 **Ctrl+Z** 로 되돌릴 수 있습니다 (일괄 수정은 한 번의 Ctrl+Z로 전체 취소).

## 검사 항목

| 카테고리 | 검사 내용 | 심각도 | 자동 수정 |
| --- | --- | --- | --- |
| 1. Missing Scripts | `GetMonoBehavioursWithMissingScriptCount > 0` 인 오브젝트 | 오류 | 제거 (`RegisterCompleteObjectUndo` → `RemoveMonoBehavioursWithMissingScript`). 원본 프리팹에 포함된 Missing Script는 프리팹 에셋에서 직접 제거하도록 안내 |
| 2. Animator & Static 충돌 | Animator가 있는 Static 오브젝트, Animator 클립이 Transform을 움직이는 Static 하위 자식 | Batching Static이면 오류, 그 외 경고 | Static 해제 (`go.isStatic = false`) |
| 3. MeshCollider & Negative Scale | `lossyScale` 음수(메시 렌더러/콜라이더 보유 시) | 경고 | 스케일 양수화 (확인 대화상자) |
| | `MeshRenderer.bounds` 중심이 피벗에서 비정상적으로 먼 경우 (임계값 초과 + 피벗이 Bounds 구 바깥) | 경고 | 수동 (모델링 툴에서 피벗 재설정) |
| | 메시가 없는 MeshCollider | 오류 | MeshFilter 메시 할당 |
| | 고폴리곤 Non-Convex MeshCollider | 경고 | 수동 |
| 4. Legacy UI Text & 깨진 TMP | `UnityEngine.UI.Text` 사용 (Dynamic Font 4096 텍스처 초과 위험) | 경고 | 수동 (Udon 참조가 끊길 수 있어 자동 변환하지 않음) |
| | `TMP_Text` 폰트 없음 / `fontSharedMaterial.mainTexture == null` (검은 사각형 버그) | 오류 | 기본 폰트 지정 / 폰트 기본 머티리얼로 복구 / Atlas 재연결 |
| 5. 거울 & 고해상도 텍스처 | 기본 활성화된 거울 | 경고 | 기본 비활성화 (확인 대화상자) |
| | Reflect Layers = Everything | 경고 | Player + MirrorReflection 으로 제한 (확인 대화상자) |
| | Disable Pixel Lights 꺼짐 | 경고 | 켜기 |
| | 씬에서 사용 중인 텍스처가 허용 크기 초과 (8192 이상은 오류) | 경고/오류 | Max Size 제한 + 재임포트 (PC/Android/iOS 오버라이드 포함) |

## 검사 설정

창의 **검사 설정 (임계값)** Foldout에서 변경하며 EditorPrefs에 저장됩니다.

- Bounds 오프셋 임계값 (기본 20m)
- MeshCollider 삼각형 경고 기준 (기본 20,000)
- 텍스처 최대 허용 크기 (기본 2048px)
- EditorOnly 태그 오브젝트 제외 (기본 켜짐 — 빌드에서 제거되므로)

## 참고 사항

- 씬 오브젝트 수정은 `Undo.RecordObject`(Missing Script는 `Undo.RegisterCompleteObjectUndo`) 후 `EditorSceneManager.MarkSceneDirty`를 호출합니다.
- 텍스처 임포트 설정은 씬이 아닌 에셋(.meta) 상태입니다. `Undo.RecordObject(importer)`로 기록하고, Undo/Redo 시 변경된 텍스처를 자동으로 다시 임포트합니다. 이전 설정값은 콘솔 로그에도 남습니다.
- 플레이 모드에서는 스캔과 자동 수정이 비활성화됩니다.
- Undo/Redo, 씬 열기/닫기, 설정 변경 후에는 결과가 오래되었다는 안내가 표시되며 [다시 스캔]으로 갱신할 수 있습니다.
