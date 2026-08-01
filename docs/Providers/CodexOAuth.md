# Codex app-server를 이용한 ChatGPT 로그인과 Canvas Assistant

SmartHopper의 `Codex Account` 탭은 OpenAI API 키 대신 로컬 Codex CLI의 app-server를 통해 ChatGPT 계정에 로그인하고 계정 상태와 플랜 한도를 확인합니다. `Canvas Assistant`와 기본 Provider를 사용하는 텍스트 AI 컴포넌트는 같은 app-server를 통해 ChatGPT 플랜으로 실행할 수 있습니다. 기존 OpenAI API provider와 API 키 설정은 변경되지 않습니다.

## 필요 조건

- Rhino 8 및 SmartHopper
- 로컬에 설치되어 실행 가능한 Codex CLI
- `codex app-server --listen stdio://`를 지원하는 Codex CLI 버전
- OAuth 로그인을 완료할 시스템 기본 브라우저

터미널에서 다음 명령으로 설치 상태를 확인할 수 있습니다.

```powershell
codex --version
codex app-server --help
```

## 사용 방법

1. SmartHopper 설정 창에서 `Codex Account` 탭을 엽니다.
2. Codex가 `PATH`에 있으면 `Codex CLI 경로`를 비워 자동 탐색을 사용합니다. 필요하면 `codex.exe`, `codex.cmd` 또는 해당 운영체제의 Codex 실행 파일을 직접 선택합니다.
3. 기존 기본 Codex 환경과 분리된 로그인이 필요할 때만 `선택적 CODEX_HOME`을 지정합니다.
4. `app-server 시작 및 연결` 또는 `Refresh`를 눌러 app-server를 시작하고 현재 계정을 조회합니다.
5. 로그아웃 상태에서는 `Sign in with ChatGPT`를 누릅니다. Codex app-server가 반환한 HTTPS 주소가 기본 브라우저에서 열립니다.
6. 브라우저에서 로그인을 완료하면 app-server의 완료 알림을 받아 계정 이메일과 ChatGPT 플랜이 자동으로 갱신됩니다.
7. `Refresh`를 누르면 단기·장기 잔여 한도와 각 초기화 시각도 함께 갱신됩니다.

## Canvas Assistant에서 사용

1. `SmartHopper Settings`의 `Canvas Assistant` 탭에서 `Assistant Provider`를 `Codex Account`로 선택하고 저장합니다.
2. Grasshopper 캔버스 오른쪽 위 SmartHopper 버튼을 누릅니다.
3. Canvas Assistant 상단에서 현재 계정에 허용된 모델과 추론 수준을 선택합니다.
4. 선택값은 즉시 저장되며 다음 메시지와 기본 Provider를 사용하는 AI 컴포넌트부터 적용됩니다.
5. 상단의 `Canvas 편집`은 새 대화마다 `읽기 전용`으로 시작합니다. 변경이 필요하면 첫 메시지를 보내기 전에 `변경 전 확인`을 선택합니다.
6. 채팅 메시지는 `thread/start`와 `turn/start`를 통해 전송되며 응답은 기존 SmartHopper 채팅창에 스트리밍됩니다.

Codex 대화 thread는 임시 thread로 생성되고 파일 시스템 sandbox는 계속 `read-only`입니다. Canvas 조회와 변경은 `smarthopper` Dynamic Tools namespace를 통해 Rhino 프로세스 안의 기존 `AIToolManager`가 수행합니다. 변경 요청은 도구 이름, 변경 요약, 영향 GUID 수를 표시하는 비동기 승인 패널에서 `적용`을 눌러야 실행됩니다. 삭제, 버튼 클릭, patch 적용, 문서 저장, 파일 및 외부 프로그램 도구는 첫 릴리스에서 노출하지 않습니다.

대화는 열릴 때 활성 Grasshopper 문서의 reference와 `DocumentID`에 고정됩니다. 이후 다른 문서를 활성화하거나 원래 문서를 닫으면 도구 실행을 중단하며 새 문서를 자동으로 수정하지 않습니다. 변경 도구는 한 번에 하나씩 UI thread에서 실행되고, 기존 도구의 Grasshopper Undo 기록을 유지하며, 실행 전후 Canvas 상태를 다시 비교해 검증된 결과만 Codex에 반환합니다.

Codex CLI가 Dynamic Tools 실험 API를 지원하지 않거나 descriptor를 거부하면 `dynamicTools` 없이 thread를 다시 시작합니다. 이 경우 채팅은 유지되지만 Canvas 도구는 사용할 수 없으며 transcript에 읽기 전용 fallback 상태가 표시됩니다.

## AI 컴포넌트에서 사용

- Canvas Assistant에서 모델과 추론 수준을 선택하면 SmartHopper의 기본 AI Provider가 `Codex Account`로 설정됩니다.
- Provider가 `Default`인 텍스트 입력·텍스트 출력 및 JSON 출력 AI 컴포넌트는 같은 모델과 추론 수준을 사용합니다.
- AI Settings 컴포넌트에서 모델이나 `reasoning_effort`를 명시하면 해당 요청의 값이 공용 설정보다 우선합니다.
- OpenAI, Anthropic 등 특정 Provider를 명시한 기존 컴포넌트는 변경되지 않습니다.
- 이미지·오디오 생성 또는 Provider 수준 function calling이 필요한 컴포넌트는 현재 Codex Account Provider 대상이 아닙니다.

로그인 진행 중에는 `Cancel login`으로 취소할 수 있습니다. 로그인 후에는 `Sign out`으로 Codex 계정에서 로그아웃할 수 있고, `Refresh`로 현재 상태를 다시 읽을 수 있습니다.

## 보안 경계

- OAuth 엔드포인트, PKCE, client ID, callback server, 토큰 저장 및 토큰 갱신은 모두 Codex app-server가 담당합니다.
- SmartHopper는 access token, refresh token, ID token 또는 Codex 인증 파일을 읽거나 저장하거나 표시하지 않습니다.
- SmartHopper 설정에는 선택적인 Codex 실행 파일 경로, 선택적인 `CODEX_HOME`, 선택한 모델 및 추론 수준만 저장됩니다.
- app-server의 stdout JSONL과 stderr 진단 스트림은 분리됩니다. 인증 payload와 인증 주소의 query string은 로그에 기록하지 않습니다.
- SmartHopper는 app-server에서 받은 주소가 절대 HTTPS 주소일 때만 기본 브라우저로 엽니다.

`CODEX_HOME`을 별도 폴더로 지정하면 터미널이나 Codex 앱에서 이미 사용 중인 로그인과 분리된 인증 상태가 만들어질 수 있습니다. 같은 로그인을 공유하려면 이 값을 비워 두십시오.

## 일반적인 오류 해결

### Codex CLI를 찾지 못함

- 새 터미널에서 `codex --version`이 성공하는지 확인합니다.
- Rhino를 다시 시작해 변경된 `PATH`를 반영하거나, `Codex CLI 경로`에 실행 파일을 직접 지정합니다.
- Windows npm 설치에서는 `codex.cmd`를 선택할 수 있습니다.

### app-server 시작 또는 연결 실패

- `codex app-server --help`가 성공하는지 확인합니다.
- 지정한 실행 파일과 `CODEX_HOME` 경로가 존재하는지 확인합니다.
- 이미 종료됐거나 오래된 Codex 프로세스가 있다면 설정 창에서 다시 연결합니다.
- 설치 버전이 너무 오래되어 stable account protocol을 지원하지 않으면 Codex CLI를 업데이트합니다.

### 브라우저가 열리지 않음

- Windows 또는 macOS의 기본 브라우저 설정을 확인합니다.
- 안전하지 않은 HTTP 주소나 잘못된 주소는 SmartHopper가 의도적으로 열지 않습니다.
- `Cancel login`을 누른 후 다시 시도합니다.

### 로그인 후 정보가 갱신되지 않음

- 브라우저에서 로그인 완료 페이지까지 진행했는지 확인합니다.
- `Refresh`를 눌러 `account/read`를 다시 요청합니다.
- 별도 `CODEX_HOME`을 사용 중이면 해당 환경에 로그인했는지 확인합니다.

## 수동 검증 절차

실제 계정이 필요한 검증은 자동 테스트에서 수행하지 않습니다.

1. 로그아웃된 테스트용 `CODEX_HOME`을 지정하거나 기존 Codex 계정에서 로그아웃합니다.
2. 설정 창을 열어 Codex 버전과 `로그아웃됨` 상태가 표시되고 `Sign in with ChatGPT`가 활성화되는지 확인합니다.
3. 로그인 버튼을 눌러 기본 브라우저가 열리고 Rhino UI가 계속 반응하는지 확인합니다.
4. `Cancel login`을 눌러 진행 상태가 해제되는지 확인한 뒤 로그인을 다시 시작합니다.
5. 브라우저 로그인을 완료하고 이메일, 플랜, `ChatGPT OAuth (Codex 관리)` 상태가 즉시 표시되는지 확인합니다.
6. `Refresh`가 동일한 계정 상태를 다시 읽는지 확인합니다.
7. `Sign out` 후 이메일과 플랜이 사라지고 로그인 버튼이 다시 활성화되는지 확인합니다.
8. Rhino를 종료한 뒤 해당 SmartHopper 세션이 시작한 Codex app-server 자식 프로세스가 남지 않는지 확인합니다.

### Canvas Dynamic Tools

1. 테스트 문서를 열고 Canvas Assistant를 시작해 연결된 Canvas 이름과 `읽기 전용` 상태가 맞는지 확인합니다.
2. 선택 객체 조회를 요청하고 승인 없이 결과가 돌아온 뒤 최종 답변까지 이어지는지 확인합니다.
3. `변경 전 확인`을 선택한 뒤 컴포넌트 추가, 이동, 연결, 연결 해제를 각각 요청합니다.
4. 승인 패널에서 `취소`한 요청이 Canvas를 바꾸지 않는지, `적용`한 요청만 바뀌고 Grasshopper Undo로 복구되는지 확인합니다.
5. 두 문서를 열고 대화가 연결된 문서가 아닌 다른 문서를 활성화한 뒤 변경을 요청합니다. 문서 불일치 오류가 표시되고 어느 문서도 바뀌지 않아야 합니다.
6. 승인 대기 및 실행 중 `Stop`을 눌러 turn interrupt와 도구 취소가 함께 동작하는지 확인합니다.
7. Rhino runtime message와 변경 후 `verification.status`를 확인하고, `VerificationFailed`인 요청을 Assistant가 성공으로 설명하지 않는지 확인합니다.

## 현재 제한 사항

- Codex Account Provider는 텍스트 입력·텍스트 출력·JSON 출력 AI 컴포넌트를 지원합니다.
- 이미지·오디오 입출력과 Provider 수준 function calling은 지원하지 않습니다.
- Dynamic Tools에는 현재 Canvas 조회 도구와 명시된 안전한 mutation allow-list만 노출됩니다. 삭제, 파일 쓰기, 외부 프로그램 실행 및 광범위한 patch 적용은 지원하지 않습니다.
- `TrustedSession`을 표현하는 정책 형식은 준비되어 있지만 첫 UI에서는 제공하지 않습니다. 변경은 항상 건별 확인합니다.
- 일반적인 postcondition은 문서 객체, 위치 및 연결 상태 지문으로 검증합니다. Rhino 8 실제 UI 동작과 Undo는 위 수동 절차로 별도 확인해야 합니다.
