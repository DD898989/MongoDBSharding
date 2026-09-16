@echo off
set SCRIPT_DIR=%~dp0
echo Deleting Kubernetes resources...
kubectl delete -f "%SCRIPT_DIR%webapi.yaml"
kubectl delete -f "%SCRIPT_DIR%mongo-setup-job.yaml"
kubectl delete -f "%SCRIPT_DIR%mongo-router.yaml"
kubectl delete -f "%SCRIPT_DIR%mongo-shards.yaml"
kubectl delete -f "%SCRIPT_DIR%mongo-configsvr.yaml"
echo All resources deleted.
