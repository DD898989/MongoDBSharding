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
        public async Task When開呼叫建立訂單API並指定以下個特定的訂單編號(int count, DataTable table)
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
            else if (targetName == "mymongos")
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
            else if (targetName == "mymongos")
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

        [When(@"我記錄所有分片唯讀副本（""(.*)"", ""(.*)"", ""(.*)"", ""(.*)""）的初始查詢計數器")]
        public void When我記錄所有分片唯讀副本的初始查詢計數器(string p1, string p2, string p3, string p4)
        {
            _activeSecondaries.Clear();
            
            // Dynamically resolve actual secondaries instead of using the hardcoded ones
            var shard1Secondaries = GetActualSecondaries("myshard1", 15562);
            var shard2Secondaries = GetActualSecondaries("myshard2", 15563);
            
            _activeSecondaries.AddRange(shard1Secondaries);
            _activeSecondaries.AddRange(shard2Secondaries);
            
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

        private List<string> GetActualSecondaries(string shardPrefix, int port)
        {
            var secondaries = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                string pod = $"{shardPrefix}-sts-{i}";
                try
                {
                    var status = GetNodeStatus(pod, port);
                    if (status.IsSecondary)
                    {
                        secondaries.Add(pod);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Warning] Failed to query status of {pod}: {ex.Message}");
                }
            }
            return secondaries;
        }

        private void VerifyPodsQuerySum(string pod1, string pod2, int expectedTotal)
        {
            string shardPrefix = pod1.StartsWith("myshard1") ? "myshard1" : "myshard2";
            int port = shardPrefix == "myshard1" ? 15562 : 15563;
            
            // Get actual secondaries for this shard
            var actualSecondaries = GetActualSecondaries(shardPrefix, port);
            
            int sum = 0;
            Console.WriteLine($"--- Profiler Query Counts for {shardPrefix} Secondaries (Comment: {_testComment}) ---");
            
            foreach (var pod in actualSecondaries)
            {
                int count = GetNodeProfileCount(pod, port, _testComment);
                sum += count;
                Console.WriteLine($"{pod} (Actual Secondary): {count} queries");
            }

            Console.WriteLine($"Total Queries on {shardPrefix} Secondaries: {sum} (Expected: {expectedTotal})");
            sum.Should().Be(expectedTotal, $"Expected exactly {expectedTotal} queries to be routed to {shardPrefix} secondaries combined, actual secondaries found: {string.Join(", ", actualSecondaries)}");
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

        #endregion
    }
}
