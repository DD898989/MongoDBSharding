# MongoDB Sharding & Read-Replica 驗證指南

本專案基於 Kubernetes (K8s) 部署，提供 MongoDB 分片 (Sharding)、分片區域 (Zone Key Range) 與讀寫分離 (Read-Replica with Secondary Preferred) 的自動化 BDD 驗證。

---

## 1. 系統架構圖 (Architecture Diagram)

### 📊 實體與資料流拓撲

```
                           ┌────────────┐
                           │   Client   │
                           └──────┬─────┘
                                  │
                    ┌─────────────┴─────────────┐
                    │                           │
            ┌───────▼───────┐           ┌───────▼───────┐
            │  mywebapi-1   │           │  mywebapi-2   │
            └───────┬───────┘           └───────┬───────┘
                    └─────────────┬─────────────┘
                                  │
                    ┌─────────────▼─────────────┐                ┌──────────────┐
                    │      mymongos-service     ├───────────────►│  myconfigsvr │
                    └──────┬────────────┬───────┘                └──────────────┘
                           │            │
             ┌─────────────┘            └──────────────┐
             │                                         │
    ┌────────▼────────────────┐    ┌───────────────────▼───────────────────────────┐
    │    mydefault-service    │    │              [ myshard1ReplSet ]              │
    │        (Default)        │    │                ┌─────────────┐                │
    └─────────────────────────┘    │                │   Primary   │                │
                                   │                └──────┬──────┘                │
                                   │             ┌─────────┴─────────┐             │
                                   │       ┌─────▼───────┐     ┌─────▼───────┐     │
                                   │       │  Secondary  │     │  Secondary  │     │
                                   │       └─────────────┘         └─────────────┘     │
                                   │                                               │
                                   │              [ myshard2ReplSet ]              │
                                   │                ┌─────────────┐                │
                                   │                │   Primary   │                │
                                   │                └──────┬──────┘                │
                                   │             ┌─────────┴─────────┐             │
                                   │       ┌─────▼───────┐     ┌─────▼───────┐     │
                                   │       │  Secondary  │     │  Secondary  │     │
                                   │       └─────────────┘         └─────────────┘     │
                                   └───────────────────────────────────────────────┘
```

### 🔌 外部存取埠口對照表

| 內部 Service / 埠口 | 外部 LoadBalancer 埠口 | 用途 |
| :--- | :--- | :--- |
| `mywebapi-service:8080` | **`8080`** | 呼叫寫入與查詢 API 進入點 |
| `mymongos-service:15564` | **`27017`** | 外部連接統一入口 (Mongos) |
| `myshard1-headless:15562` | **`27018`** | 直接連接分片一 (驗證實體分佈) |
| `myshard2-headless:15563` | **`27019`** | 直接連接分片二 (驗證實體分佈) |
| `mydefault-service:15565` | **`27020`** | 直接連接預設分片 (驗證未分片資料) |

---

## 2. BDD 實際執行驗證邏輯

BDD 測試使用 Reqnroll 框架（規格與驗證場景寫於 [`ShardingAndReadReplica.feature`](./MongoDBSharding.Test/Features/ShardingAndReadReplica.feature)），透過直接連線資料庫與 K8s 容器內部數據進行雙重驗證：

| BDD 測試場景 | 系統核心功能價值 (At a Glance) | 具體執行與物理驗證方式 |
| :--- | :--- | :--- |
| **場景 1：連線與端口驗證** | **網路連通性保障**<br>驗證外部 Client 是否能暢通存取所有的資料庫實體分片與 API。 | TCP Client 直接嘗試建立連線：<br>• `8080` (API 服務)<br>• `27017` ~ `27020` (各資料庫節點) |
| **場景 2：API 負載平衡** | **流量高可用**<br>驗證 API 服務能均勻地將流量分發至兩個不同的執行實體 (Pod)。 | 1. 發送 100 次 API 請求並強制重連。<br>2. 檢查 HTTP Header `X-Instance-Name`。<br>3. 斷言兩台 API 主機流量分配均介於 **30% ~ 70%**。 |
| **場景 3：分片區域與資料隔離** | **資料精準分流**<br>• 未分片集合（國家）100% 存在 Default 庫。<br>• 已分片集合（訂單）按 Hashed 分佈到對應 Shard，Default 庫無訂單。 | 1. 建立國家，連入 `mydefault` 驗證有資料；連入 `myshard` 驗證無資料。<br>2. 建立訂單，連入 `myshard1` 驗證有 A 訂單；連入 `myshard2` 驗證有 B 訂單，而 `mydefault` 皆無。 |
| **場景 4：副本讀寫分離** | **讀取效能擴充 (Read Scalability)**<br>驗證寫入操作進入主庫，但**讀取操作會自動分流至各分片的 Secondary (唯讀) 節點**。 | 1. 測試自動找出 `myshard` 所有 **Secondary (唯讀)** 節點。<br>2. 啟動 Secondary 的 Profiler 監控。<br>3. 經 API 發送 33 次帶有 Guid Comment 的讀取請求。<br>4. 斷言並驗證：這 33 次讀取 100% 被記錄在 Secondary，而 Primary 完全沒有收到查詢。 |

---

## 3. 跨語言切換與業務邏輯修改 (依照實際業務邏輯修改 Collection 與 Document 結構)

若需將 C# ([`Program.cs`](./MongoDBSharding/Program.cs)) 改為其他語言，請參考下方最簡實作，並請務必依實際業務邏輯修改 Collection 與 Document 結構：

### 🟢 Node.js (Express + Native MongoDB Driver)
```javascript
const client = new MongoClient('mongodb://mymongos-service:15564', {
    readPreference: ReadPreference.SECONDARY_PREFERRED
});
const db = client.db('MyShardingDb');

// 查詢時傳遞 Comment (集合名稱與欄位請依實際業務邏輯修改)
const order = await db.collection('Orders').findOne(
    { OrderId: orderId }, 
    comment ? { comment: comment } : {}
);
```

### 🐍 Python (FastAPI + Motor)
```python
from fastapi import FastAPI
from motor.motor_asyncio import AsyncIOMotorClient
from bson import MinKey, MaxKey

app = FastAPI()
client = AsyncIOMotorClient("mongodb://mymongos-service:15564/?readPreference=secondaryPreferred")
db = client["MyShardingDb"]

@app.on_event("startup")
async def setup_sharding():
    # 啟動時設定分片 (★ 集合名稱與分片鍵請依實際業務邏輯修改)
    try:
        await client["admin"].command({"shardCollection": "MyShardingDb.Orders", "key": {"OrderId": "hashed"}})
        await client["admin"].command({"updateZoneKeyRange": "MyShardingDb.Orders", "min": {"OrderId": MinKey()}, "max": {"OrderId": MaxKey()}, "zone": "my_zone"})
    except Exception: pass

@app.get("/api/sharding/order/get")
async def get_order(orderId: str, comment: str = None):
    # 重要：必須將 comment 傳入，以便 BDD Secondary Profiler 統計 (集合名稱請依業務修改)
    opts = {"$comment": comment} if comment else {}
    return await db["Orders"].find_one({"OrderId": orderId}, **opts)
```
