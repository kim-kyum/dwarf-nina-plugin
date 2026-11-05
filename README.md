# DWARF NINA Plugin

DWARF 스마트 망원경(DWARF 3)을 **N.I.N.A.에 직접** 연결하는 플러그인입니다.
별도 브리지 프로그램(dwarfAlp, ASCOM Alpaca 서버 등) 없이, NINA 장비 목록에서 고르기만 하면 됩니다.

> ⚠️ **비공식·실험 단계**입니다. DWARFLAB과 관련이 없으며, 펌웨어 업데이트로 동작이 달라질 수 있습니다.
> 시험할 때는 망원경 옆에서 지켜봐 주세요.

## 진행 상황

| 단계 | 내용 | 상태 |
|---|---|---|
| 1 | 카메라: 연결, 단일 노출 촬영, FITS를 NINA로 가져오기 | **시험 중** |
| 2 | 필터휠, 포커서 | 예정 |
| 3 | 망원경 GoTo, 자동 보정 | 예정 |
| 4 | 안정화, 배포 | 예정 |

## 설치 (1단계 시험판)

1. [Actions](../../actions)에서 최신 성공 빌드를 열고 **DWARF-NINA-Plugin**을 내려받아 압축을 풉니다.
2. NINA를 끈 상태에서 **`Install-DWARF-Plugin.cmd`를 더블클릭**합니다.
   (`%LOCALAPPDATA%\NINA\Plugins\3.0.0\DWARF`에 자동으로 복사됩니다.)
3. NINA를 켜고 **Options → Plugins → Installed**에 DWARF가 있는지 확인한 뒤,
   **장비 → 카메라**에서 **DWARF 3 Camera**를 골라 [연결]합니다.
   - DWARF는 자동으로 찾습니다. 찾지 못하면 카메라 옆 설정(톱니) 버튼에서 IP를 직접 입력하세요.
   - 연결 전에 **휴대폰 DWARFLAB 앱을 완전히 종료**하세요.

NINA **3.0 이상**이 필요합니다.

## 문제가 생기면: DwarfProbe

NINA 없이 연결과 사진 1장 촬영만 시험하는 도구입니다 (`DwarfProbe.zip`).

```
DwarfProbe.exe                       자동 검색 + 1초 노출 1장
DwarfProbe.exe --ip 192.168.0.23     IP 지정
DwarfProbe.exe --exp 5 --gain 80     노출/게인 지정
DwarfProbe.exe --connect-only        연결만 확인
```

실행 폴더에 로그(`dwarfprobe-*.log`)와 FITS 파일이 생깁니다. 문제 보고 시 로그를 첨부해 주세요.

## 구조

```
src/Dwarf.Core          DWARF 통신 라이브러리 (WebSocket+protobuf, HTTP, FTP, FITS)
src/NINA.Plugin.Dwarf   NINA 플러그인 (ICamera 드라이버)
tools/DwarfProbe        단독 시험 도구
tests/MockDwarf         개발용 가짜 DWARF 서버 (실기기 없이 전체 흐름 시험)
```

빌드: `dotnet build DwarfNina.sln` (.NET 8). Windows용 배포 파일은 GitHub Actions가 만듭니다.

## 라이선스와 출처

GPL-3.0. DWARF 통신 방식(명령 순서, 메시지 정의 `.proto`, 파일 위치 등)은
[dwarfAlp](https://github.com/acocalypso/dwarfAlp) (GPL-3.0, acocalypso)의 분석과 실기기 검증 결과를 C#으로 옮긴 것입니다.
