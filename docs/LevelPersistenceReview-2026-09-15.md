# 데디케이트 서버 급식 레벨 하락 검토

2026-09-15. 사용자는 CreatureManager와 최신 FeedLikeGrandma를 사용하며 zone unload/reload 이후 하락을 의심한다고 설명했다. **현재 FeedLikeGrandma 1.1.3 + 확인한 CreatureManager 1.1.13 조합에는 이 증상을 설명하는 구체적인 상태 불일치 경로가 있다.** 원격 제보 서버의 개체를 직접 재현한 것은 아니지만, 로컬 소스뿐 아니라 실제 설치 CreatureManager DLL의 해당 분기와 호출을 IL로 확인했다.

이번 검토에서 코드는 수정하지 않았고 빌드·배포·게임 실행을 하지 않았다. 이전 미커밋 수정은 보존했다. 앞선 Valheim 1.0.12 API 호환성 검사와는 별개인 모드 간 상태 보존 문제다.

## 확인한 버전과 범위

- 작업 트리 및 기존 Release: FeedLikeGrandma 1.1.3. 최종 DLL SHA-256 `823315F87BCE8E63E27F5BD4B6677503CF326863402A7B9D3CF02A97BC7F8D85`.
- `C:/Users/blizz/RiderProjects/CreatureManager`의 관련 소스 및 게임 plugins의 CreatureManager 1.1.13. 설치 DLL SHA-256 `04E4F5CA564C92BD2A8090222A9A9B74AE8F596FAD0A68F3A0BB7B1D0D16FEBA`.
- 로컬 LogOutput.log에 CreatureManager 1.1.13, FeedLikeGrandma 1.1.3, Valheim 1.0.12 로딩 기록이 있다. 이는 제보 서버의 정확한 DLL·설정 확인을 대신하지 않는다.
- 게임 원본: 보존된 Windows x64 1.0.12 클라이언트 b25253764, 데디케이트 b25253791. 전역 INDEX와 기존 `ilspy-9.1.0.7988-r1` 추출을 재사용했다. 게임 DLL을 publicize하거나 재추출하지 않았다.
- 범위: 급식 레벨 계산/적용, owner RPC 경로, Character 레벨 저장/읽기, CreatureManager 외부 SetLevel 처리·출처 분류·재로드 복원, 성장/Orb의 레벨 사용. 전체 외부 모드 감사, 원격 월드 데이터·개별 실패 로그, Unity/멀티플레이 실험은 제외했다.

## 우선 원인: 서로 다른 두 저장값

| 값 | 담당 코드 | 의미 |
| --- | --- | --- |
| `Character.m_level` | 게임 Character.GetLevel | 해당 프로세스에서 현재 사용하는 레벨 |
| ZDO `ZDOVars.s_level` | 게임 Character.SetLevel/Awake | 게임의 저장·네트워크 레벨 |
| ZDO `CreatureManager_DesiredLevel` | CreatureLevelManager | CreatureManager가 복원할 별도 목표 레벨 |

1. `FeedSystem.ApplyStarProgress`(`FeedSystem.cs:1779`, SetLevel 호출 `:1808`)는 `character.GetLevel()+1`을 계산해 `FeedCreatureModifierCompatibility.SetLevelPreservingCllcModifiers`(`:71`)를 호출한다. 이 함수는 게임 SetLevel과 CLLC 특성 보존을 수행한다. CreatureManager 목표 레벨을 직접 갱신하지 않는다.
2. 게임 `Character.cs:775`의 SetLevel은 메모리 레벨과 ZDO `s_level`을 모두 갱신한다. 따라서 급식 직후 게임 저장값 자체가 무조건 누락되는 문제는 아니다.
3. CreatureManager의 `Character.SetLevel` Last Postfix(`CreatureHarmonyPatches.cs:1198`)는 `RestoreConfiguredLevel`을 호출한다. 그러나 `CreatureLevelManager.cs:1109`의 `ShouldPreserveExistingRolls` 분기는 체력 배율 복원 후 즉시 반환한다. 외부 새 레벨을 `CreatureManager_DesiredLevel`에 반영하는 `TryAdoptExternalLevelOverride(:1218)`에 도달하지 않는다.
4. 이 보존 분기는 **모든 길들인 동물에 무조건 적용되는 것은 아니다.** 출처가 Growup 또는 TamedRestore인 경우다(`CreatureLevelManager.cs:1564`). `CreatureManagerSpawnLifecycle.GetSpawnSource(Character)`(`CreatureHarmonyPatches.cs:888`)는 저장된 출처와 현재 인스턴스의 ManagedSpawn 기록이 없으면 길들인 개체를 TamedRestore로 취급한다. 일반 자연 생성 개체는 새 인스턴스로 재로드되면서 이 조건에 들어갈 수 있다. Breeding/Egg/Command 등 저장된 출처가 있으면 다른 정책을 따른다.
5. 존 재로드 시 게임 Awake는 새 `s_level`을 읽지만, CreatureManager의 Character/Humanoid.Start Postfix(`CreatureHarmonyPatches.cs:1044/1053`) → TryApplyLevel → TryApplyLevelState(`CreatureLevelManager.cs:325`) → SynchronizeStoredRuntimeState(`:850`)가 다시 실행된다. 이 함수는 **기존 DesiredLevel이 있으면 s_level보다 우선**해 `character.m_level`에 대입한다. owner가 아닌 인스턴스도 pending 처리에서 같은 복원 함수를 거칠 수 있다(`:209/222`).

결과적으로 “특성의 기존 추첨값 유지” 정책이 “외부 급식으로 새로 정한 레벨도 이전 값으로 복원”하는 결과로 이어진다. `RestoreConfiguredLevel`, `SynchronizeStoredRuntimeState`, 두 GetSpawnPolicy, GetSpawnSource, TryApplyLevelState 및 Harmony Postfix의 해당 연결을 설치 DLL에서 Mono.Cecil로 확인했다.

## 제보를 설명하는 상태 예시

아래는 확인한 분기의 상태 추적 예시이며 실제 게임 실행 결과가 아니다. 내부 레벨은 별 개수+1이다. 전제는 CreatureManager의 목표 레벨 키가 존재하고, 급식 시 Growup/TamedRestore 보존 정책이 적용되는 살아 있는 길들인 개체다.

| 단계 | 메모리 레벨 | 게임 ZDO 레벨 | CM 목표 레벨 |
| --- | ---: | ---: | ---: |
| 길들이기 후 존 재로드, 급식 전 | 1 | 1 | 1 |
| 먹이를 여러 번 주어 2성까지 상승 | 3 | 3 | 1 |
| 다시 존 unload/reload | **1** | 3 | 1 |
| 그 상태에서 다시 급식해 한 단계 상승 | 2 | **2** | 1 |

재로드 직후에는 원래 높은 게임 저장값이 아직 남을 수 있다. 그러나 단순 별 표시만의 문제로 볼 수 없다. 실제 GetLevel()이 낮아지고, 조건에 따라 체력 복원도 낮은 레벨로 계산된다(`CreatureLevelManager.ApplyHealthMultiplier:2206`). 이후 FeedLikeGrandma가 낮은 메모리 값을 기준으로 다음 레벨을 계산하면 실제 게임 저장값까지 낮아질 수 있다.

Orb `CapturePayload`(`TamingOrbSystem.cs:382`)도 GetLevel()을 기록하므로 이미 낮아진 런타임 레벨을 포획 데이터에 고정할 수 있다. 성장/번식 역시 부모/현재 GetLevel을 사용하지만, 이 모드는 Growup이 있는 유년 개체에는 급식 별 상승을 주지 않는다(`FeedSystem.BuildFeedEffectPlan:1231`). 따라서 “같은 급식에서 성장과 레벨업을 동시에 적용해 유실”하는 설명은 현재 코드의 주원인으로 삼지 않았다.

## 보조 위험과 제외한 설명

- 게임 GetLevel은 메모리 값이고 Character.Awake 이외에 ZDO 레벨 변경을 계속 반영하는 기본 경로가 없다. FeedLikeGrandma에도 별도의 Character 레벨 수신/owner 전환 보정은 없다. 이미 생성된 다른 peer의 인스턴스가 이전 레벨을 유지하는 문제는 CM의 오래된 DesiredLevel 문제와 구분해 검증해야 한다. 이 부분만으로 제보 전부를 단정하지 않는다.
- 수동 급식은 서버가 현재 owner로 prepare/commit을 전달하고 owner 여부를 검사한다(`FeedManualTransactionSystem.cs:432/590/618`). 자동 급식도 owner에서 적용한다(`FeedSystem.cs:1152`). “데디케이트에는 로컬 플레이어가 없어 레벨을 저장하지 않는다”는 설명은 현재 경로와 맞지 않는다.
- 최대 레벨 설정은 추가 상승 가능 여부를 제한한다. 설정 재로드나 시간이 지났다는 이유로 현재 레벨을 일괄 낮추는 FLG 코드는 발견하지 못했다.
- 배고픔/굶주림에 따른 자동 레벨 하락 기능도 해당 적용 경로에 없다. 서버 저장 파일 자체가 실제로 롤백됐는지는 원격 자료가 없어 판정하지 않았다.

## 최소 수정 권고 — 이번에는 미적용

1. **우선 CreatureManager의 외부 레벨 채택 경계를 수정한다.** owner가 외부 SetLevel을 받은 경우 Growup/TamedRestore의 특성 보존 정책을 유지하면서 새 레벨을 CM 목표 레벨에 반영해야 한다. 기존 `TryAdoptExternalLevelOverride`를 무조건 호출하면 ReapplyLevelDependentRuntimeState를 통해 체력/공격력 정책이 재평가될 수 있으므로, 특성 보존과 레벨 기록을 구분해야 한다. FLG가 다른 모드의 private ZDO 키를 임의로 쓰는 우회보다 기록을 소유한 CM에서 고치는 것이 적절하다.
2. **이미 불일치한 개체의 복구는 별도 작은 단계로 다룬다.** `s_level`이 높고 DesiredLevel만 오래된 경우 해당 권한/출처 범위에서 실제 게임 레벨을 존중하도록 한다. 두 값의 max를 무조건 선택하는 전역 보정은 관리자·다른 모드의 의도적 하향 변경을 무시하므로 피한다. 이미 s_level/Orb까지 낮아진 경우 과거 레벨을 현재 상태만으로 정확히 복원할 수 있다고 보장하지 않는다.
3. **다중 peer의 런타임 레벨 갱신을 독립 검증한다.** owner 변경 전후의 ZDO와 메모리 불일치를 해결하되 비owner에서 SetLevel을 무조건 호출하면 ZDO 쓰기·체력 초기화·외부 패치가 발생한다. 읽기/표시 갱신과 owner의 영속 저장을 구분한다. 매 프레임 reflection이나 새 무조건 broadcast RPC는 먼저 도입하지 않는다.

네트워크 인증·거래 ID·중복 결과·음식 escrow·미확정 commit의 환불 방어를 변경할 필요는 없다. 이번 문제 때문에 식량을 자동 환불하거나 최고 레벨을 별도 FLG 저장 형식으로 중복 보관하는 방식은 권고하지 않는다.

## 검증 순서와 완료 범위

실행 재현 시 같은 개체의 ZDOID, owner, spawn source, `GetLevel()`, `s_level`, `CreatureManager_DesiredLevel`, LevelApplied/ProcessingComplete를 급식 전후·존 재로드 전후에 비교해야 한다. 원격 서버 데이터는 현재 읽지 않았고 이 값을 찍는 진단 패치도 설치하지 않았다.

1. 복사본 월드에서 CM이 레벨을 부여한 일반 동물을 길들인다. 한 번 존을 완전히 내렸다 올린 뒤 급식한다. 이 순서가 TamedRestore 분기를 확인하는 데 중요하다.
2. 급식으로 레벨을 여러 번 올린 다음 모든 관찰자가 영역을 떠나 실제 언로드를 유도하고 돌아온다. 잠깐 거리를 벌린 것만으로 언로드됐다고 가정하지 않는다.
3. 세 값의 불일치와 별/최대 체력을 확인하고, 추가 급식 및 Orb 포획은 복사본에서만 시험한다. 저장/재접속으로 낮은 값이 영속화되는지 구분한다.
4. 자연 생성·성장한 성체·교배/알·관리자 소환의 출처별 사례, owner A→B 이전, 클라이언트 호스트와 데디케이트를 나눠 검증한다. 수정 후에는 기존 특성·체력 배율·음식 개수·거래 중복 처리도 보존되는지 확인한다.

수행: 관련 C# 호출/분기 검토, 게임 원본의 메모리/저장 레벨 경로 확인, 설치 CM DLL의 해당 IL/메타데이터 및 해시 확인, 로컬 로그의 버전 확인.

미수행: 이번 빌드, 새 자동 테스트, 실제 게임/데디케이트 재현, 원격 제보 개체 분석. 기존 YAML/Orb 37개 회귀 검사는 이 CM 상태 충돌을 실행하지 않으므로 이 문제의 검증으로 대체하지 않았다.
