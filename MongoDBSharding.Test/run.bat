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

echo.
echo ---------------------------------------------------
echo  步驟 1: 清除現有的 K8s 資源 (不保留任何狀態)...
echo ---------------------------------------------------
echo Deleting Kubernetes resources...
kubectl delete -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\webapi.yaml"
kubectl delete -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\mongo-setup-job.yaml"
kubectl delete -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\mongo-router.yaml"
kubectl delete -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\mongo-shards.yaml"
kubectl delete -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\mongo-configsvr.yaml"
echo All resources deleted.

echo.
echo ---------------------------------------------------
echo  步驟 2: 等待 K8s 資源與 Pod 釋放完全 (無限迴圈偵測)...
echo ---------------------------------------------------
:wait_release
powershell -NoProfile -Command "$p = kubectl get pods -l 'app in (webapi,mongos,shard1,shard2,mydefault,configsvr)' --no-headers 2>$null; if ($p) { exit 1 } else { exit 0 }"
if %errorlevel% neq 0 (
    echo 偵測到 K8s Pod 仍在釋放中，等待 2 秒後重試...
    timeout /t 2 > nul
    goto wait_release
)
echo K8s 資源與 Pod 已完全釋放！

echo.
echo ---------------------------------------------------
echo  步驟 3: 重新部署 K8s 環境 (Fresh Start)...
echo ---------------------------------------------------
echo Building webapi docker image...
docker build -t webapi:latest "%SCRIPT_DIR%..\MongoDBSharding"

echo Applying Kubernetes manifests...
kubectl apply -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\mongo-configsvr.yaml"
kubectl apply -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\mongo-shards.yaml"
kubectl apply -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\mongo-router.yaml"

echo Deleting existing setup job if any...
kubectl delete job mongodb-setup --ignore-not-found

echo Deploying MongoDB setup job...
kubectl apply -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\mongo-setup-job.yaml"

echo Deploying webapi...
kubectl apply -f "%SCRIPT_DIR%..\MongoDBSharding\k8s\webapi.yaml"
echo Deployment completed!

echo.
echo ---------------------------------------------------
echo  步驟 4: 等待 Web API 服務就緒...
echo ---------------------------------------------------
:wait_api
powershell -NoProfile -Command "try { $r = Invoke-WebRequest -Uri 'http://localhost:8080/swagger' -UseBasicParsing -TimeoutSec 2; if ($r.StatusCode -eq 200 -and $r.Content -match 'Swagger UI') { exit 0 } } catch { } exit 1"
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
