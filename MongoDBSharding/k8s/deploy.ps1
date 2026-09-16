$projectRoot = Split-Path -Parent $PSScriptRoot

docker build -t mywebapi:latest $projectRoot

kubectl apply -f "$PSScriptRoot/mongo-configsvr.yaml"
kubectl apply -f "$PSScriptRoot/mongo-shards.yaml"
kubectl apply -f "$PSScriptRoot/mongo-router.yaml"
kubectl apply -f "$PSScriptRoot/mongo-setup-job.yaml"
kubectl apply -f "$PSScriptRoot/webapi.yaml"

Write-Host "Deployment completed!" -ForegroundColor Green
