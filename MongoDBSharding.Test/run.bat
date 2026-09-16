@echo off
title MongoDB Sharding BDD Test - Clean Loop Mode
chcp 65001 > nul
set SCRIPT_DIR=%~dp0

set LOOP_COUNT=1

:loop
echo.
echo =======================================================================
echo  [第 %LOOP_COUNT% 次循環測試] 開始時間: %time%
echo =======================================================================






echo Deleting Kubernetes resources...

REM kubectl delete -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\webapi.yaml"
REM kubectl delete -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\mongo-setup-job.yaml"
REM kubectl delete -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\mongo-router.yaml"
REM kubectl delete -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\mongo-shards.yaml"
REM kubectl delete -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\mongo-configsvr.yaml"

kubectl delete all --all

echo All resources deleted.






:wait_release
powershell -NoProfile -Command "$p = kubectl get pods -l 'app in (mywebapi-pod,mymongos-pod,myshard1-pod,myshard2-pod,mydefault-pod,myconfigsvr-pod,mymongodb-setup-pod)' --no-headers 2>$null; if ($p) { exit 1 } else { exit 0 }"
if %errorlevel% neq 0 (
    echo 偵測到 K8s Pod 仍在釋放中，等待 2 秒後重試...
    timeout /t 2 > nul
    goto wait_release
)
echo K8s 資源與 Pod 已完全釋放！








docker build -t mywebapi:latest "%SCRIPT_DIR%..\MongoDBSharding"

kubectl apply -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\mongo-configsvr.yaml"
kubectl apply -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\mongo-shards.yaml"
kubectl apply -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\mongo-router.yaml"
kubectl apply -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\mongo-setup-job.yaml"
kubectl apply -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\webapi.yaml"

echo Deployment completed!











:wait_api
powershell -NoProfile -Command "try { $r = Invoke-WebRequest -Uri 'http://localhost:8080/api/sharding/instance' -UseBasicParsing -TimeoutSec 2; if ($r.StatusCode -eq 200 -and $r.Content -match 'InstanceName') { exit 0 } } catch { } exit 1"
if %errorlevel% neq 0 (
    echo Web API 尚未就緒，等 1 秒後重試...
    timeout /t 1 > nul
    goto wait_api
)
echo Web API 已上線，所有分片初始化就緒！



echo.
echo ---------------------------------------------------
echo  步驟 5: 執行 BDD 物理分片驗證測試...
echo ---------------------------------------------------
dotnet test "%SCRIPT_DIR%MongoDBSharding.Test.csproj" --logger:"console;verbosity=normal"

if %errorlevel% equ 0 (
    echo.
    echo [第 %LOOP_COUNT% 次循環測試結果]: 成功
) else (
    echo.
    echo [第 %LOOP_COUNT% 次循環測試結果]: 失敗
)

set /a LOOP_COUNT+=1
echo.
echo -----------------------------------------------------------------------
echo [第 %LOOP_COUNT% 次循環就緒] 請按任意鍵 (ENTER) 進行下一輪全新重部署與測試...
echo -----------------------------------------------------------------------
pause > nul
goto loop
