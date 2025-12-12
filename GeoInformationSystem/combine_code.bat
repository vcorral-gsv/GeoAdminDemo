@echo off
setlocal EnableExtensions
set /a INCLUDED=0

rem =========================
rem Config
rem =========================
set "BASE_DIR=%~dp0"
for %%I in ("%BASE_DIR%.") do set "BASE_DIR=%%~fI\"

set "OUTPUT=%BASE_DIR%code.txt"
if exist "%OUTPUT%" del /q "%OUTPUT%"
type nul > "%OUTPUT%"

rem Extensiones permitidas (sin punto)
set "ALLOWED_EXT=cs js ts py"

rem Excluir directorios típicos (subcadenas en la ruta)
set "EXCLUDE_DIRS=node_modules .git bin obj dist build out .next .expo"

rem Excluir *.d.ts (1 = sí, 0 = no)
set "EXCLUDE_DTS=1"

rem =========================
rem Args
rem =========================
if "%~1"=="" (
  echo Uso: %~nx0 [ruta_relativa_carpeta_o_archivo] [ruta2] [...]
  exit /b 1
)

:nextArg
if "%~1"=="" goto endArgs
call :ProcessPath "%~1"
shift
goto nextArg

goto :eof

rem ==========================================================
rem ProcessPath  %1 => carpeta o archivo (relativo o absoluto)
rem ==========================================================
:ProcessPath
set "INPUT=%~1"
set "TARGET=%INPUT%"

rem Si no tiene unidad (C:\), lo colgamos de BASE_DIR
for %%P in ("%TARGET%") do (
  if "%%~dP"=="" (
    set "TARGET=%BASE_DIR%%INPUT%"
  )
)

for %%Q in ("%TARGET%") do set "TARGET=%%~fQ"

if exist "%TARGET%\" (
  echo Procesando carpeta: "%TARGET%"
  echo.

  for /r "%TARGET%" %%F in (*) do (
    call :HandleFile "%%~fF"
  )
  goto :eof
)

if exist "%TARGET%" (
  echo Procesando archivo: "%TARGET%"
  echo.
  call :HandleFile "%TARGET%"
  goto :eof
)

echo [ADVERTENCIA] Ruta no encontrada: "%INPUT%"
echo.
goto :eof

rem ==========================================================
rem HandleFile %1 => archivo absoluto
rem ==========================================================
:HandleFile
set "FILE=%~1"

rem ---- Excluir por directorios ----
call :IsExcludedDir "%FILE%"
if errorlevel 1 exit /b 0

rem ---- Filtrar extensión ----
call :HasAllowedExtension "%FILE%"
if errorlevel 1 (
  rem echo SKIP EXT: "%FILE%"
  exit /b 0
)


rem ---- Excluir d.ts ----
if "%EXCLUDE_DTS%"=="1" (
  call :IsDts "%FILE%"
  if not errorlevel 1 exit /b 0
)

rem ---- Escribir ----
call :AppendFile "%FILE%"
exit /b 0

rem ==========================================================
rem IsExcludedDir: ERRORLEVEL 1 si excluido
rem ==========================================================
:IsExcludedDir
set "P=%~1"
set "P=%P:\=/%"
set "P=/%P%/"

for %%D in (%EXCLUDE_DIRS%) do (
  echo %P% | findstr /I /C:"/%%D/" >nul && exit /b 1
)
exit /b 0


rem ==========================================================
rem HasAllowedExtension: ERRORLEVEL 0 si permitida, 1 si no
rem ==========================================================
:HasAllowedExtension
for %%F in ("%~1") do (
  for %%E in (%ALLOWED_EXT%) do (
    if /I "%%~xF"==".%%E" exit /b 0
  )
)
exit /b 1


rem ==========================================================
rem IsDts: ERRORLEVEL 0 si ES .d.ts, 1 si NO es .d.ts
rem ==========================================================
:IsDts
for %%F in ("%~1") do set "NAME=%%~nxF"
if /I "%NAME:~-5%"==".d.ts" exit /b 0
exit /b 1

rem ==========================================================
rem AppendFile
rem ==========================================================
:AppendFile
set "ABS=%~1"
call set "REL=%%ABS:%BASE_DIR%=%%"

>>"%OUTPUT%" echo [%REL%]
type "%ABS%" >> "%OUTPUT%"
>>"%OUTPUT%" echo.
>>"%OUTPUT%" echo ----------------------------------
>>"%OUTPUT%" echo.
set /a INCLUDED+=1

exit /b 0

:endArgs
echo.
echo Archivo generado en: "%OUTPUT%"
echo.

dir "%OUTPUT%"
echo.
for %%A in ("%OUTPUT%") do echo Tamano: %%~zA bytes

echo.
echo Archivos incluidos: %INCLUDED%
pause
endlocal
