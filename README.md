# VRC-World-Checker
VRChat 3D 월드 배포 전, 버튼 하나로 프로젝트의 무결성과 최적화 상태를 진단하고 자동 수정(Auto-Fix)할 수 있는 Unity Custom EditorWindow 스크립트입니다.

## 이 도구의 초점

씬 디스크립터·라이팅·포스트 프로세싱 같은 전반적인 설정 점검은 기존 도구(VRWorld Toolkit의 World Debugger 등)에 맡기고, 이 도구는 **"업로드 후에야 드러나는 깨짐"을 업로드 전에 잡아내고, 고친 결과를 수치로 확인하는 것**에 집중합니다. 두 도구를 함께 쓰는 것을 전제로 합니다.

- **씬 전체 참조 무결성**: 모든 컴포넌트의 직렬화 필드를 훑어 Missing 참조(Udon 공개 변수·UI 이벤트 대상 포함), 원본이 사라진 프리팹, 프로그램이 없는 UdonBehaviour, 빈 머티리얼 슬롯, 셰이더를 찾을 수 없는 머티리얼을 필드 경로 단위로 보여 줍니다.
- **애니메이션을 이해하는 Static 분석**: 클립 바인딩(Transform·`m_IsActive`·프로퍼티)을 해석해, 실제로 애니메이션되는 오브젝트에 켜진 Static 플래그만 짚어 냅니다.
- **지오메트리 무결성**: 음수 스케일, 피벗 이탈, 메시 없는 MeshCollider, 완전히 겹친 중복 메시(Z-fighting·드로우콜 낭비)를 찾습니다. 중복은 다른 오브젝트가 참조하지 않는 경우에만 자동 삭제합니다.
- **되돌릴 수 있는 자동 수정**: 씬 수정은 Undo 그룹(일괄 수정도 Ctrl+Z 한 번)으로, 텍스처 축소는 원본 백업과 [원본 크기로 복원]으로 되돌립니다.
- **성과 측정과 공유**: 스캔마다 월드 지표(삼각형·머티리얼·텍스처 메모리·실시간 라이트)를 기록해 이전 스캔과 비교하고, 결과를 Markdown 리포트로 복사·저장해 이슈나 PR에 붙일 수 있습니다.

## 설치

1. `Assets/Editor/VRCWorldPreFlightChecker.cs` 파일을 VRChat 월드 프로젝트의 `Assets/Editor/` 폴더에 복사합니다.
2. Unity 메뉴 **Tools > VRC World Pre-Flight Checker** 를 엽니다.

- 대상 환경: Unity 2022.3 LTS + VRChat SDK3 Worlds
- 의존성: `UnityEditor`, `UnityEngine`, `UnityEngine.UI`, `TMPro` 만 사용. VRChat SDK에 컴파일 의존성이 없습니다 (거울·UdonBehaviour는 타입 이름과 직렬화 필드로 식별).

## 사용법

1. **[전체 월드 스캔 실행 (Run Full Scan)]** 을 누르면 로드된 모든 씬의 GameObject(비활성 포함)를 6개 카테고리로 검사합니다.
2. 대시보드에서 오류/경고/수정 완료 개수와 **월드 지표**를 확인합니다. 두 번째 스캔부터는 **이전 스캔 대비** 변화가 함께 표시됩니다.
3. 카테고리별 Foldout에서 각 항목의 **[Select]** (선택 및 Ping) 또는 **[Fix]** (자동 수정) 버튼을 사용합니다. 카테고리 헤더의 **[일괄 제거]/[일괄 수정]** 으로 한 번에 수정할 수도 있습니다.
4. 수정 후 하단의 **[씬·에셋 저장]** 으로 씬과 수정된 에셋(TMP 머티리얼 등)을 저장하고, 다시 스캔해 지표 변화를 확인합니다.
5. **[리포트 복사]** / **[리포트 저장 (.md)]** 으로 결과를 공유합니다.

## 검사 항목

| 카테고리 | 검사 내용 | 심각도 | 자동 수정 |
| --- | --- | --- | --- |
| 1. Missing Scripts | `GetMonoBehavioursWithMissingScriptCount > 0` 인 오브젝트 | 오류 | 제거 (`RegisterCompleteObjectUndo` → `RemoveMonoBehavioursWithMissingScript`). 원본 프리팹에 포함된 것은 프리팹 에셋에서 제거하도록 안내 |
| 2. Animator & Static 충돌 | Animator 루트 본체가 Static인 경우와, 클립 바인딩(`GetCurveBindings` + `GetObjectReferenceCurveBindings`)이 가리키는 Static 오브젝트. Transform 이동·`m_IsActive` 토글은 하위 자식에게도 전파되는 것으로 판정 | Batching Static + 이동/루트, Occluder Static + `m_IsActive` 토글이면 오류, 그 외 경고 | Static 해제 |
| 3. MeshCollider & Negative Scale | `lossyScale` 음수 (메시 렌더러/콜라이더 보유 시) | 경고 | 스케일 양수화 (확인 대화상자) |
| | `MeshRenderer.bounds` 중심이 피벗에서 비정상적으로 먼 경우 | 경고 | 수동 (피벗 재설정) |
| | 메시가 없는 MeshCollider / 고폴리곤 Non-Convex MeshCollider | 오류 / 경고 | MeshFilter 메시 할당 / 수동 |
| | 메시·머티리얼·레이어·그림자 모드·월드 행렬(1mm 단위)이 모두 같은 중복 오브젝트 | 경고 | 중복 삭제 (확인 대화상자, Undo 가능). 다른 컴포넌트가 참조 중이거나, 하위 오브젝트·추가 컴포넌트·원본에 없는 콜라이더가 있거나, 프리팹 내부·Animator 하위면 수동 |
| 4. Legacy UI Text & 깨진 TMP | `UnityEngine.UI.Text` 사용 (Dynamic Font 4096 텍스처 초과 위험) | 경고 | 수동 (Udon 참조 보호를 위해 자동 변환하지 않음) |
| | `TMP_Text` 폰트 없음 / `fontSharedMaterial.mainTexture == null` (검은 사각형 버그) | 오류 | 기본 폰트 지정 / ① 현재 머티리얼에 Atlas 재연결(프리셋 유지) ② 폰트 기본 머티리얼로 교체 ③ 기본 머티리얼에 Atlas 재연결 |
| 5. 거울 & 고해상도 텍스처 | 기본 활성화된 거울, `m_ReflectLayers` = Everything, `m_DisablePixelLights` 꺼짐 | 경고 | 기본 비활성화 / Player + MirrorReflection 제한 / 켜기 (`SerializedObject.ApplyModifiedProperties`) |
| | 씬에서 사용 중인 텍스처가 허용 크기 초과 (8192 이상은 오류) | 경고/오류 | 확인 후 Max Size 제한 + 재임포트 (PC/Android/iOS 오버라이드 포함), 원본 값 백업 |
| 6. 참조 무결성 | 직렬화 필드의 Missing 참조 (값은 null인데 Instance ID가 남은 참조 — 필드 경로와 Udon 공개 변수/UnityEvent 출처 표시) | 오류 | 수동 |
| | 원본 에셋이 사라진 Missing Prefab | 오류 | 수동 |
| | Program Source가 없는 UdonBehaviour | 오류 | 수동 |
| | 메시가 있는 렌더러의 빈 머티리얼 슬롯, 셰이더를 찾을 수 없는 머티리얼 (분홍색 렌더링) | 오류 | 수동 |

## 월드 지표와 리포트

- **월드 지표**: 활성 렌더러의 삼각형 수, 고유 머티리얼 수, 씬에서 쓰는 텍스처 메모리(에디터 기준 추정), 실시간/혼합 라이트 수를 스캔마다 기록합니다.
- **이전 스캔 대비**: 오류·경고·텍스처 메모리·삼각형·머티리얼 변화를 `before → after` 로 보여 줍니다.
- **자동 수정 효과(예상)**: 다시 스캔하기 전에도 이번에 적용한 수정으로 줄어드는 텍스처 메모리와 드로우콜 추정치를 표시합니다.
- **Markdown 리포트**: 요약, 월드 지표 표(이전 스캔 포함), 카테고리별 이슈 표(대상 경로·내용·상태)를 클립보드로 복사하거나 `.md` 파일로 저장합니다.

## 텍스처 원본 크기 복원

`TextureImporter.SaveAndReimport()`는 디스크의 `.meta` 파일을 직접 바꾸므로, Undo에 묶으면 일반 씬 작업 중 Ctrl+Z가 예기치 않은 재임포트나 에디터 멈춤을 일으킬 수 있습니다. 그래서 텍스처 축소는 Undo로 관리하지 않습니다.

- 축소 전에 확인 대화상자를 띄우고, 원본 값을 `Dictionary<string, int>` (에셋 경로 → 기존 `maxTextureSize`)에 백업합니다. 플랫폼 오버라이드 값은 별도 딕셔너리(`"경로|플랫폼"` → 값)에 백업합니다.
- 여러 번 축소해도 **최초 원본 값**이 유지되며, 백업은 `SessionState`에 저장되어 스크립트 리컴파일 후에도 남습니다 (에디터를 닫으면 삭제).
- 카테고리 5의 **축소한 텍스처 원본 백업** 목록에서 **[원본 크기로 복원 (Restore)]** 또는 **[모두 복원]** 으로 되돌립니다.

## 검사 설정

창의 **검사 설정 (임계값)** Foldout에서 변경하며 EditorPrefs에 저장됩니다.

- Bounds 오프셋 임계값 (기본 20m)
- MeshCollider 삼각형 경고 기준 (기본 20,000)
- 텍스처 최대 허용 크기 (기본 2048px)
- EditorOnly 태그 오브젝트 제외 (기본 켜짐 — 빌드에서 제거되므로)

## 참고 사항

- 씬 오브젝트 수정은 `Undo.RecordObject`(Missing Script는 `Undo.RegisterCompleteObjectUndo`, 중복 삭제는 `Undo.DestroyObjectImmediate`) 후 `EditorSceneManager.MarkSceneDirty`를 호출합니다.
- 거울 설정은 `new SerializedObject(mirror)` + `FindProperty("m_ReflectLayers" / "m_DisablePixelLights")`로 읽고 `ApplyModifiedProperties()`로 수정합니다 (Undo·프리팹 오버라이드를 Unity가 직접 처리).
- TMP Atlas 재연결은 공유 머티리얼 에셋을 수정합니다 (Undo 가능). 머티리얼의 `_TextureWidth`/`_TextureHeight`가 Atlas 크기와 다르면 다른 폰트용 프리셋으로 보고 재연결하지 않습니다.
- 참조 무결성 검사는 모든 컴포넌트의 직렬화 필드를 순회하므로 큰 씬에서는 수 초가 걸릴 수 있습니다 (진행 바 표시). 원시 타입 배열과 내부 프리팹 연결 필드는 건너뜁니다.
- 플레이 모드에서는 스캔과 자동 수정이 비활성화되며, Undo/Redo·씬 열기/닫기·설정 변경 후에는 [다시 스캔] 안내가 표시됩니다.
