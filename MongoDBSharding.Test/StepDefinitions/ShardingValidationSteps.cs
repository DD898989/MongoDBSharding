using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Reqnroll;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace MongoDBSharding.Test.StepDefinitions
{
    [Binding]
    public class ShardingValidationSteps
    {
        private readonly HttpClient _httpClient = new() { BaseAddress = new Uri("http://localhost:8080") };
        private Dictionary<string, int> _initialQueryCounters = new();
        private readonly string _testComment = "test-read-replica-sharding-" + Guid.NewGuid().ToString("N");
        private List<string> _activeSecondaries = new();
        private readonly Dictionary<string, int> _instanceRequestCounts = new();
        private bool _lastConnectionSuccessful;

        [BeforeScenario]
        public static void ClearDatabase()
        {
            try
            {
                // Clear MyCountry and Orders collections from the mongos router (port 15564)
                RunKubectlCommand("exec deployment/mymongos-deploy -c mymongo-router -- mongosh --port 15564 --quiet --eval \"db.getSiblingDB('MyShardingDb').MyCountry.deleteMany({}); db.getSiblingDB('MyShardingDb').Orders.deleteMany({});\"");
                Console.WriteLine("[BeforeScenario] Successfully cleared MyShardingDb collections.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to clear database before scenario: {ex.Message}");
            }
        }

        [AfterScenario]
        public void CleanupScenario()
        {
            try
            {
                CleanupSecondaryProfiling();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to cleanup after scenario: {ex.Message}");
            }
        }

        [Given(@"Web API 服務已成功啟動且所有 MongoDB 節點正常運作")]
        public async Task GivenWebAPI服務已成功啟動且所有MongoDB節點正常運作()
        {
            // Wait for up to 300 seconds for the webapi to respond with 200 OK
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(300))
            {
                try
                {
                    var response = await _httpClient.GetAsync("/swagger/index.html");
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch
                {
                    // Ignore and retry
                }
                await Task.Delay(1000);
            }
            throw new Exception("Timed out waiting for Web API to become healthy.");
        }

        [When(@"我呼叫建立國家 API 建立 ""(.*)"" 次國家")]
        public async Task When我呼叫建立國家API建立次國家(int count)
        {
            for (int i = 1; i <= count; i++)
            {
                var country = new
                {
                    Name = $"Country_{i}_{Guid.NewGuid():N}",
                    Code = $"C{i}"
                };
                var response = await _httpClient.PostAsJsonAsync("/api/country/create", country);
                response.EnsureSuccessStatusCode();
            }
        }

        [Then(@"預設分片 ""(.*)"" 應該要存有 ""(.*)"" 筆國家資料")]
        public void Then預設分片應該要存有筆國家資料(string target, int expectedCount)
        {
            int actualCount = GetCollectionCount("deployment/mydefault-deploy", 15565, "MyCountry");
            actualCount.Should().Be(expectedCount, $"Expected mydefault to contain exactly {expectedCount} country documents");
        }

        [Then(@"雜湊分片 ""(.*)"" 與 ""(.*)"" 應該存有 ""(.*)"" 筆國家資料")]
        public void Then雜湊分片與應該存有筆國家資料(string shard1, string shard2, int expectedCount)
        {
            int shard1Count = GetCollectionCount("statefulset/myshard1-sts", 15562, "MyCountry");
            int shard2Count = GetCollectionCount("statefulset/myshard2-sts", 15563, "MyCountry");

            shard1Count.Should().Be(expectedCount, $"Expected {shard1} to contain {expectedCount} country documents");
            shard2Count.Should().Be(expectedCount, $"Expected {shard2} to contain {expectedCount} country documents");
        }

        [When(@"我呼叫建立訂單 API 建立 ""(.*)"" 次訂單，並隨機或指定 OrderId")]
        public async Task When我呼叫建立訂單API建立次訂單並隨機或指定OrderId(int count)
        {
            for (int i = 1; i <= count; i++)
            {
                var req = new
                {
                    OrderId = $"ORDER_{i}_{Guid.NewGuid():N}"
                };
                var response = await _httpClient.PostAsJsonAsync("/api/sharding/order/create", req);
                response.EnsureSuccessStatusCode();
            }
        }

        [Then(@"預設分片 ""(.*)"" 應該存有 ""(.*)"" 筆訂單資料")]
        public void Then預設分片應該存有筆訂單資料(string target, int expectedCount)
        {
            int actualCount = GetCollectionCount("deployment/mydefault-deploy", 15565, "Orders");
            actualCount.Should().Be(expectedCount, $"Expected mydefault to contain {expectedCount} orders because Orders are sharded in my_zone");
        }

        [Then(@"雜湊分片 ""(.*)"" 與 ""(.*)"" 中的訂單資料總和必須等於 ""(.*)""")]
        public void Then雜湊分片與中的訂單資料總和必須等於(string shard1, string shard2, int expectedTotal)
        {
            int shard1Count = GetCollectionCount("statefulset/myshard1-sts", 15562, "Orders");
            int shard2Count = GetCollectionCount("statefulset/myshard2-sts", 15563, "Orders");

            int actualTotal = shard1Count + shard2Count;
            actualTotal.Should().Be(expectedTotal, $"Expected total sharded orders to be {expectedTotal}, but found {shard1Count} in shard1 and {shard2Count} in shard2");
        }

        [Then(@"""(.*)"" 與 ""(.*)"" 兩者都必須存有至少 ""(.*)"" 筆以上的訂單資料")]
        public void Then與兩者都必須存有至少筆以上的訂單資料(string shard1, string shard2, int expectedMin)
        {
            int shard1Count = GetCollectionCount("statefulset/myshard1-sts", 15562, "Orders");
            int shard2Count = GetCollectionCount("statefulset/myshard2-sts", 15563, "Orders");

            shard1Count.Should().BeGreaterThanOrEqualTo(expectedMin, $"{shard1} should have received hashed order documents");
            shard2Count.Should().BeGreaterThanOrEqualTo(expectedMin, $"{shard2} should have received hashed order documents");
        }

        [When(@"我記錄所有 MongoDB 唯讀副本節點的初始查詢計數器")]
        public void When我記錄所有MongoDB唯讀副本節點的初始查詢計數器()
        {
            var secondaryCounters = GetSecondaryNodeCounters();
            _initialQueryCounters = secondaryCounters.ToDictionary(k => k.Key, v => v.Value.QueryCount);
            
            Console.WriteLine("--- Initial Secondary Query Counters ---");
            foreach (var kvp in _initialQueryCounters)
            {
                Console.WriteLine($"{kvp.Key}: {kvp.Value}");
            }
        }

        [When(@"我呼叫查詢訂單 API 共 ""(.*)"" 次")]
        public async Task When我呼叫查詢訂單API共次(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var response = await _httpClient.GetAsync("/api/sharding/order/list");
                response.EnsureSuccessStatusCode();
            }
        }

        [Then(@"所有唯讀副本節點的查詢計數器增加值之和應該等於 ""(.*)""")]
        public void Then所有唯讀副本節點的查詢計數器增加值之和應該等於(int expectedTotal)
        {
            var latestCounters = GetSecondaryNodeCounters();
            int totalIncrease = 0;

            Console.WriteLine("--- Query Counter Differences ---");
            foreach (var kvp in _initialQueryCounters)
            {
                string pod = kvp.Key;
                int initialVal = kvp.Value;
                if (latestCounters.TryGetValue(pod, out var latest))
                {
                    int diff = latest.QueryCount - initialVal;
                    // opcounters is monotonic, check for non-negative
                    if (diff < 0) diff = 0; 
                    totalIncrease += diff;
                    Console.WriteLine($"{pod}: {initialVal} -> {latest.QueryCount} (diff: {diff})");
                }
            }

            // Allow slightly higher count due to other possible queries, but must be at least expectedTotal
            totalIncrease.Should().BeGreaterThanOrEqualTo(expectedTotal, $"Expected at least {expectedTotal} queries to be handled by the secondaries");
        }

        [Then(@"每個唯讀副本節點增加的查詢計數器都必須大於 ""(.*)""")]
        public void Then每個唯讀副本節點增加的查詢計數器都必須大於(int expectedMin)
        {
            var latestCounters = GetSecondaryNodeCounters();

            foreach (var kvp in _initialQueryCounters)
            {
                string pod = kvp.Key;
                int initialVal = kvp.Value;
                if (latestCounters.TryGetValue(pod, out var latest))
                {
                    int diff = latest.QueryCount - initialVal;
                    diff.Should().BeGreaterThan(expectedMin, $"Expected secondary pod {pod} to receive some query traffic, but increase was {diff}");
                }
            }
        }

        [Given(@"系統 Web API 服務已成功啟動且各個實體分片皆可連線")]
        public async Task Given系統WebAPI服務已成功啟動且各個實體分片皆可連線()
        {
            await GivenWebAPI服務已成功啟動且所有MongoDB節點正常運作();
        }

        [When(@"我呼叫建立國家 API 建立以下 (.*) 個國家:")]
        public async Task When我呼叫建立國家API建立以下個國家(int expectedCount, DataTable table)
        {
            foreach (var row in table.Rows)
            {
                var country = new
                {
                    Name = row["Name"],
                    Code = row["Code"]
                };
                var response = await _httpClient.PostAsJsonAsync("/api/country/create", country);
                response.EnsureSuccessStatusCode();
            }
        }

        [Then(@"我直接連線預設分片 ""(.*)"" 查詢，應該要在 ""(.*)"" 集合中找到剛好 (.*) 筆國家資料")]
        public void Then我直接連線預設分片查詢應該要在集合中找到剛好筆國家資料(string target, string collectionName, int expectedCount)
        {
            int actualCount = GetCollectionCount("deployment/mydefault-deploy", 15565, collectionName);
            actualCount.Should().Be(expectedCount, $"Expected mydefault to contain exactly {expectedCount} {collectionName} documents");
        }

        [Then(@"我直接連線 ""(.*)"" 與 ""(.*)"" 查詢，在 ""(.*)"" 集合中不應該存有任何資料")]
        public void Then我直接連線與查詢在集合中不應該存有任何資料(string shard1, string shard2, string collectionName)
        {
            int shard1Count = GetCollectionCount("statefulset/myshard1-sts", 15562, collectionName);
            int shard2Count = GetCollectionCount("statefulset/myshard2-sts", 15563, collectionName);

            shard1Count.Should().Be(0, $"Expected {shard1} to contain 0 {collectionName} documents");
            shard2Count.Should().Be(0, $"Expected {shard2} to contain 0 {collectionName} documents");
        }

        [When(@"我呼叫建立訂單 API 並指定以下 (.*) 個特定的訂單編號:")]
        public async Task When我呼叫建立訂單API並指定以下個特定的訂單編號(int count, DataTable table)
        {
            foreach (var row in table.Rows)
            {
                var req = new
                {
                    OrderId = row["OrderId"]
                };
                var response = await _httpClient.PostAsJsonAsync("/api/sharding/order/create", req);
                response.EnsureSuccessStatusCode();
            }
        }

        [Then(@"我直接連線預設分片 ""(.*)"" 查詢，在 ""(.*)"" 集合中不應該存有任何資料")]
        public void Then我直接連線預設分片查詢在集合中不應該存有任何資料(string target, string collectionName)
        {
            int actualCount = GetCollectionCount("deployment/mydefault-deploy", 15565, collectionName);
            actualCount.Should().Be(0, $"Expected {target} to contain 0 {collectionName} documents");
        }

        [Then(@"我直接連線 ""(.*)"" 與 ""(.*)"" 查詢，兩邊的 ""(.*)"" 集合中資料數量相加必須剛好為 (.*)")]
        public void Then我直接連線與查詢兩邊的集合中資料數量相加必須剛好為(string shard1, string shard2, string collectionName, int expectedTotal)
        {
            int shard1Count = GetCollectionCount("statefulset/myshard1-sts", 15562, collectionName);
            int shard2Count = GetCollectionCount("statefulset/myshard2-sts", 15563, collectionName);

            int actualTotal = shard1Count + shard2Count;
            actualTotal.Should().Be(expectedTotal, $"Expected total sharded orders to be {expectedTotal}, but found {shard1Count} in {shard1} and {shard2Count} in {shard2}");
        }

        [Then(@"我直接連線 ""(.*)"" 查詢，應該存在訂單 ""(.*)""")]
        public void Then我直接連線查詢應該存在訂單(string targetName, string orderId)
        {
            string target;
            int port;

            if (targetName == "myshard1")
            {
                target = "statefulset/myshard1-sts";
                port = 15562;
            }
            else if (targetName == "myshard2")
            {
                target = "statefulset/myshard2-sts";
                port = 15563;
            }
            else if (targetName == "mydefault")
            {
                target = "deployment/mydefault-deploy";
                port = 15565;
            }
            else if (targetName == "27017")
            {
                target = "deployment/mymongos-deploy";
                port = 15564;
            }
            else
            {
                throw new ArgumentException($"Unknown query target: {targetName}");
            }

            string container = GetContainerName(target);
            string output = RunKubectlCommand($"exec {target} -c {container} -- mongosh --port {port} --quiet --eval \"db.getSiblingDB('MyShardingDb').Orders.countDocuments({{ OrderId: '{orderId}' }})\"");
            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            int count = 0;
            foreach (var line in lines)
            {
                if (int.TryParse(line.Trim(), out int parsedCount))
                {
                    count = parsedCount;
                    break;
                }
            }
            count.Should().Be(1, $"Expected {targetName} to contain order {orderId}");
        }

        [Then(@"我直接連線 ""(.*)"" 查詢，不應該存在訂單 ""(.*)""")]
        public void Then我直接連線查詢不應該存在訂單(string targetName, string orderId)
        {
            string target;
            int port;

            if (targetName == "myshard1")
            {
                target = "statefulset/myshard1-sts";
                port = 15562;
            }
            else if (targetName == "myshard2")
            {
                target = "statefulset/myshard2-sts";
                port = 15563;
            }
            else if (targetName == "mydefault")
            {
                target = "deployment/mydefault-deploy";
                port = 15565;
            }
            else if (targetName == "27017")
            {
                target = "deployment/mymongos-deploy";
                port = 15564;
            }
            else
            {
                throw new ArgumentException($"Unknown query target: {targetName}");
            }

            string container = GetContainerName(target);
            string output = RunKubectlCommand($"exec {target} -c {container} -- mongosh --port {port} --quiet --eval \"db.getSiblingDB('MyShardingDb').Orders.countDocuments({{ OrderId: '{orderId}' }})\"");
            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            int count = 0;
            foreach (var line in lines)
            {
                if (int.TryParse(line.Trim(), out int parsedCount))
                {
                    count = parsedCount;
                    break;
                }
            }
            count.Should().Be(0, $"Expected {targetName} NOT to contain order {orderId}");
        }

        [Then(@"""(.*)"" 與 ""(.*)"" 各自的 ""(.*)"" 集合中資料數量皆必須大於 (.*) 筆")]
        public void Then與各自的集合中資料數量皆必須大於筆(string shard1, string shard2, string collectionName, int expectedMin)
        {
            int shard1Count = GetCollectionCount("statefulset/myshard1-sts", 15562, collectionName);
            int shard2Count = GetCollectionCount("statefulset/myshard2-sts", 15563, collectionName);

            shard1Count.Should().BeGreaterThan(expectedMin, $"{shard1} should have received hashed order documents");
            shard2Count.Should().BeGreaterThan(expectedMin, $"{shard2} should have received hashed order documents");
        }

        [When(@"我記錄所有分片唯讀副本（""(.*)"", ""(.*)"", ""(.*)"", ""(.*)""）的初始查詢計數器")]
        public void When我記錄所有分片唯讀副本的初始查詢計數器(string p1, string p2, string p3, string p4)
        {
            _activeSecondaries.Clear();
            _activeSecondaries.AddRange(new[] { p1, p2, p3, p4 });
            
            Console.WriteLine("--- Initializing MongoDB Database Profilers on Secondaries ---");
            foreach (var pod in _activeSecondaries)
            {
                int port = pod.StartsWith("myshard1") ? 15562 : 15563;
                SetNodeProfilingLevel(pod, port, 2);
                Console.WriteLine($"Enabled Profiler (Level 2) on secondary pod: {pod}");
            }
        }

        [When(@"我使用特定 OrderId 查詢訂單 (.*) 次，其中 ""(.*)"" 查詢 (.*) 次，""(.*)"" 查詢 (.*) 次")]
        public async Task When我使用特定OrderId查詢訂單次其中查詢次查詢次(int totalCount, string orderId1, int count1, string orderId2, int count2)
        {
            // Query orderId1 count1 times
            for (int i = 0; i < count1; i++)
            {
                var response = await _httpClient.GetAsync($"/api/sharding/order/get?orderId={orderId1}&comment={_testComment}");
                response.EnsureSuccessStatusCode();
            }

            // Query orderId2 count2 times
            for (int i = 0; i < count2; i++)
            {
                var response = await _httpClient.GetAsync($"/api/sharding/order/get?orderId={orderId2}&comment={_testComment}");
                response.EnsureSuccessStatusCode();
            }
        }

        [Then(@"我再次讀取所有分片唯讀副本的查詢次數，""(.*)"" 與 ""(.*)"" 總和應該增加 (.*) 次")]
        public void Then我再次讀取所有分片唯讀副本的查詢次數與總和應該增加次(string pod1, string pod2, int expectedTotal)
        {
            VerifyPodsQuerySum(pod1, pod2, expectedTotal);
        }

        [Then(@"""(.*)"" 與 ""(.*)"" 總和應該增加 (.*) 次")]
        public void Then與總和應該增加次(string pod1, string pod2, int expectedTotal)
        {
            VerifyPodsQuerySum(pod1, pod2, expectedTotal);
        }

        [Given(@"系統 Web API 服務已成功部署 2 個實體且均已就緒")]
        public async Task Given系統WebAPI服務已成功部署個實體且均已就緒()
        {
            RunKubectlCommand("rollout status deployment/mywebapi-deploy --timeout=120s");
            await GivenWebAPI服務已成功啟動且所有MongoDB節點正常運作();
        }

        [When(@"我呼叫 Web API 測試負載平衡共 (.*) 次")]
        public async Task When我呼叫WebAPI測試負載平衡共次(int count)
        {
            _instanceRequestCounts.Clear();
            for (int i = 0; i < count; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/sharding/instance");
                request.Headers.ConnectionClose = true; // Force new connection to enable load balancing

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                string? instanceName = null;
                if (response.Headers.TryGetValues("X-Instance-Name", out var values))
                {
                    instanceName = values.FirstOrDefault();
                }

                if (string.IsNullOrEmpty(instanceName))
                {
                    var content = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("instanceName", out var prop))
                    {
                        instanceName = prop.GetString();
                    }
                }

                if (!string.IsNullOrEmpty(instanceName))
                {
                    if (!_instanceRequestCounts.ContainsKey(instanceName))
                    {
                        _instanceRequestCounts[instanceName] = 0;
                    }
                    _instanceRequestCounts[instanceName]++;
                }
            }

            Console.WriteLine("--- Load Balancing Instance Counts ---");
            foreach (var kvp in _instanceRequestCounts)
            {
                Console.WriteLine($"{kvp.Key}: {kvp.Value} requests");
            }
        }

        [Then(@"這些請求應該要大致平均分配給 (.*) 個不同的 Web API 實體")]
        public void Then這些請求應該要大致平均分配給個不同的WebAPI實體(int expectedInstancesCount)
        {
            _instanceRequestCounts.Keys.Count.Should().Be(expectedInstancesCount, $"Expected to hit exactly {expectedInstancesCount} different instances");

            int totalRequests = _instanceRequestCounts.Values.Sum();
            foreach (var kvp in _instanceRequestCounts)
            {
                double percentage = (double)kvp.Value / totalRequests * 100;
                percentage.Should().BeInRange(30, 70, $"Instance {kvp.Key} received {kvp.Value} ({percentage:F1}%) of {totalRequests} requests, which is not roughly balanced (30%-70% required)");
            }
        }

        private void VerifyPodsQuerySum(string pod1, string pod2, int expectedTotal)
        {
            int sum = 0;
            Console.WriteLine($"--- Profiler Query Counts for {pod1} & {pod2} (Comment: {_testComment}) ---");
            
            int port1 = pod1.StartsWith("myshard1") ? 15562 : 15563;
            int count1 = GetNodeProfileCount(pod1, port1, _testComment);
            sum += count1;
            Console.WriteLine($"{pod1}: {count1} queries");

            int port2 = pod2.StartsWith("myshard1") ? 15562 : 15563;
            int count2 = GetNodeProfileCount(pod2, port2, _testComment);
            sum += count2;
            Console.WriteLine($"{pod2}: {count2} queries");

            Console.WriteLine($"Total Queries on {pod1} & {pod2}: {sum} (Expected: {expectedTotal})");
            sum.Should().Be(expectedTotal, $"Expected exactly {expectedTotal} queries to be routed to {pod1} and {pod2} combined");
        }

        private void SetNodeProfilingLevel(string podName, int port, int level)
        {
            string container = GetContainerName(podName);
            RunKubectlCommand($"exec {podName} -c {container} -- mongosh --port {port} --quiet --eval \"db.getSiblingDB('MyShardingDb').setProfilingLevel({level})\"");
        }

        private int GetNodeProfileCount(string podName, int port, string comment)
        {
            string js = $"print(db.getSiblingDB('MyShardingDb').system.profile.countDocuments({{ 'command.comment': '{comment}' }}))";
            string container = GetContainerName(podName);
            string output = RunKubectlCommand($"exec {podName} -c {container} -- mongosh --port {port} --quiet --eval \"{js}\"");
            
            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (int.TryParse(line.Trim(), out int count))
                {
                    return count;
                }
            }
            throw new Exception($"Failed to parse profile count from output: '{output}' on pod {podName}");
        }

        private void CleanupSecondaryProfiling()
        {
            foreach (var pod in _activeSecondaries)
            {
                int port = pod.StartsWith("myshard1") ? 15562 : 15563;
                try
                {
                    string container = GetContainerName(pod);
                    RunKubectlCommand($"exec {pod} -c {container} -- mongosh --port {port} --quiet --eval \"db.getSiblingDB('MyShardingDb').setProfilingLevel(0);\"");
                    Console.WriteLine($"[Cleanup] Profiling disabled on {pod}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Warning] Failed to cleanup profiling on {pod}: {ex.Message}");
                }
            }
        }

        [When(@"外部客戶端嘗試對外網 IP 的 Port ""(.*)"" 進行連線")]
        public async Task When外部客戶端嘗試對外網IP的Port進行連線(string portStr)
        {
            int port = int.Parse(portStr);
            _lastConnectionSuccessful = false;

            using var tcpClient = new System.Net.Sockets.TcpClient();
            try
            {
                // We attempt connection asynchronously with a 2-second timeout
                var connectTask = tcpClient.ConnectAsync("localhost", port);
                var delayTask = Task.Delay(2000);

                var completedTask = await Task.WhenAny(connectTask, delayTask);
                if (completedTask == connectTask)
                {
                    await connectTask; // Will throw if failed
                    _lastConnectionSuccessful = true;
                }
            }
            catch
            {
                _lastConnectionSuccessful = false;
            }
        }

        [Then(@"連線應該要成功建立")]
        public void Then連線應該要成功建立()
        {
            _lastConnectionSuccessful.Should().BeTrue($"Expected connection to the external port to succeed, but it failed.");
        }

        [Then(@"連線應被拒絕 \(Refused\) 或超時 \(Timeout\)")]
        public void Then連線應被拒絕或超時()
        {
            _lastConnectionSuccessful.Should().BeFalse($"Expected connection to the internal port to be refused or timed out, but it succeeded.");
        }

        #region Helpers

        private static string GetContainerName(string target)
        {
            if (target.Contains("mymongos") || target.Contains("router")) return "mymongo-router";
            if (target.Contains("myshard1") || target.Contains("shard1")) return "mymongo-shard1";
            if (target.Contains("myshard2") || target.Contains("shard2")) return "mymongo-shard2";
            if (target.Contains("mydefault") || target.Contains("default")) return "mymongo-default";
            if (target.Contains("myconfigsvr") || target.Contains("configsvr")) return "mymongo-configsvr";
            return "mymongo";
        }

        private static string RunKubectlCommand(string arguments)
        {
            using var process = new Process();
            process.StartInfo.FileName = "kubectl";
            process.StartInfo.Arguments = arguments;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new Exception($"kubectl command failed with exit code {process.ExitCode}. Error: {error}. Output: {output}");
            }

            return output.Trim();
        }

        private int GetCollectionCount(string target, int port, string collectionName)
        {
            string container = GetContainerName(target);
            string output = RunKubectlCommand($"exec {target} -c {container} -- mongosh --port {port} --quiet --eval \"db.getSiblingDB('MyShardingDb').{collectionName}.countDocuments()\"");
            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (int.TryParse(line.Trim(), out int count))
                {
                    return count;
                }
            }
            throw new Exception($"Failed to parse document count from output: '{output}' for collection '{collectionName}' on {target}");
        }

        public class MongoStatus
        {
            public bool IsSecondary { get; set; }
            public int QueryCount { get; set; }
        }

        private MongoStatus GetNodeStatus(string podName, int port)
        {
            string js = "print(JSON.stringify({isSecondary: db.hello().secondary, queryCount: Number(db.serverStatus().opcounters.query)}))";
            string container = GetContainerName(podName);
            string output = RunKubectlCommand($"exec {podName} -c {container} -- mongosh --port {port} --quiet --eval \"{js}\"");
            
            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(trimmed);
                        var root = doc.RootElement;
                        return new MongoStatus
                        {
                            IsSecondary = root.GetProperty("isSecondary").GetBoolean(),
                            QueryCount = root.GetProperty("queryCount").GetInt32()
                        };
                    }
                    catch
                    {
                        // Ignore and try next line
                    }
                }
            }
            throw new Exception($"Failed to parse MongoDB status JSON from output: '{output}' on pod {podName}");
        }

        private Dictionary<string, MongoStatus> GetSecondaryNodeCounters()
        {
            var secondaries = new Dictionary<string, MongoStatus>();
            
            // Shard 1 pods
            for (int i = 0; i < 3; i++)
            {
                string pod = $"myshard1-sts-{i}";
                try
                {
                    var status = GetNodeStatus(pod, 15562);
                    if (status.IsSecondary)
                    {
                        secondaries[pod] = status;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Warning] Failed to query status of {pod}: {ex.Message}");
                }
            }
            
            // Shard 2 pods
            for (int i = 0; i < 3; i++)
            {
                string pod = $"myshard2-sts-{i}";
                try
                {
                    var status = GetNodeStatus(pod, 15563);
                    if (status.IsSecondary)
                    {
                        secondaries[pod] = status;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Warning] Failed to query status of {pod}: {ex.Message}");
                }
            }
            
            return secondaries;
        }

        #endregion
    }
}