# language: zh-TW
功能: MongoDB 讀寫分離與實體分片精準驗證
  為了確保國家資料（Unsharded）與訂單資料（Sharded with Zone）能正確地路由至目標實體分片，
  並且驗證讀寫分離機制（secondaryPreferred）能將讀取流量均勻分配到所有的唯讀副本（Secondary 節點），
  作為一個系統架構工程師，
  我希望在 Web API 上進行寫入與查詢操作，並直接透過實體資料庫連線與 K8s 狀態來驗證其物理分佈。

  場景: 1. 驗證國家資料僅存在於預設分片而訂單資料均勻分佈於分片一與分片二
    假設 系統 Web API 服務已成功啟動且各個實體分片皆可連線
    當 我呼叫建立國家 API 建立以下 2 個國家:
      | Name | Code |
      | 台灣 | TW   |
      | 日本 | JP   |
    那麼 我直接連線預設分片 "mydefault" 查詢，應該要在 "MyCountry" 集合中找到剛好 2 筆國家資料
    而且 我直接連線 "shard1" 與 "shard2" 查詢，在 "MyCountry" 集合中不應該存有任何資料

    當 我呼叫建立訂單 API 並指定以下 2 個特定的訂單編號:
      | OrderId     | CustomerName | Amount |
      | ORDER_ID_03 | Guest_D      | 90.0   |
      | ORDER_ID_05 | Guest_E      | 150.7  |
    那麼 我直接連線 "mydefault" 查詢，不應該存在訂單 "ORDER_ID_03"
    而且 我直接連線 "mydefault" 查詢，不應該存在訂單 "ORDER_ID_05"
    而且 我直接連線 "shard1" 查詢，應該存在訂單 "ORDER_ID_03"
    而且 我直接連線 "shard1" 查詢，不應該存在訂單 "ORDER_ID_05"
    而且 我直接連線 "shard2" 查詢，應該存在訂單 "ORDER_ID_05"
    而且 我直接連線 "shard2" 查詢，不應該存在訂單 "ORDER_ID_03"
    而且 我直接連線 "27017" 查詢，應該存在訂單 "ORDER_ID_03"
    而且 我直接連線 "27017" 查詢，應該存在訂單 "ORDER_ID_05"

  場景: 2. 驗證多次特定 OrderId 查詢時流量均勻路由至對應 Shard 的唯讀副本 (Secondary)
    假設 系統 Web API 服務已成功啟動且各個實體分片皆可連線
    當 我呼叫建立國家 API 建立以下 1 個國家:
      | Name | Code |
      | 台灣 | TW   |
    而且 我呼叫建立訂單 API 並指定以下 2 個特定的訂單編號:
      | OrderId     |
      | ORDER_ID_03 |
      | ORDER_ID_05 |
    而且 我記錄所有分片唯讀副本（"shard1-1", "shard1-2", "shard2-1", "shard2-2"）的初始查詢計數器
    而且 我使用特定 OrderId 查詢訂單 33 次，其中 "ORDER_ID_03" 查詢 11 次，"ORDER_ID_05" 查詢 22 次
    那麼 我再次讀取所有分片唯讀副本的查詢次數，"shard1-1" 與 "shard1-2" 總和應該增加 11 次
    而且 "shard2-1" 與 "shard2-2" 總和應該增加 22 次
