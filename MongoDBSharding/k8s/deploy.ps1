# Get the script's parent directory (project root)
$projectRoot = Split-Path -Parent $PSScriptRoot

Write-Host "Building webapi docker image..." -ForegroundColor Green
docker build -t webapi:latest $projectRoot

Write-Host "Applying Kubernetes manifests..." -ForegroundColor Green
kubectl apply -f "$PSScriptRoot/mongo-configsvr.yaml"
kubectl apply -f "$PSScriptRoot/mongo-shards.yaml"
kubectl apply -f "$PSScriptRoot/mongo-router.yaml"

# Delete any existing setup job first to ensure it runs again
Write-Host "Deleting existing setup job if any..." -ForegroundColor Yellow
kubectl delete job mongodb-setup --ignore-not-found

Write-Host "Deploying MongoDB setup job..." -ForegroundColor Green
kubectl apply -f "$PSScriptRoot/mongo-setup-job.yaml"

Write-Host "Deploying webapi..." -ForegroundColor Green
kubectl apply -f "$PSScriptRoot/webapi.yaml"

Write-Host "Deployment completed!" -ForegroundColor Green
