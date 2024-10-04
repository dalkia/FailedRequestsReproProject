using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;

public class DecentralandABWearableFetcher : MonoBehaviour
{
    private const string oldReferenceSnapshotContentURL = "https://peer.decentraland.org/content/contents/bafybeihdtxww224vlx7trjy4hj74aoqskdvbuajeuvlhlmbopz5hfdqaua";

    private const string snapshotURL = "https://peer.decentraland.org/content/snapshots";
    private const string contentsURL = "https://peer.decentraland.org/content/contents/";
    private const string manifestUrlTemplate = "https://ab-cdn.decentraland.org/manifest/{0}_mac.json";
    private const string assetBundleUrlTemplate = "https://ab-cdn.decentraland.org/{0}/{1}";
    private const string cacheFilePath = "AssetBundleCache.json"; // Path to save the cache file

    private CancellationTokenSource ct;
    private Dictionary<Hash128, string> assetBundleCache = new Dictionary<Hash128, string>(); // Cache for storing URL and hash

    private int totalFilesDownloaded;
    private int totalFilesToDownload = 13_000;
    void Start()
    {
        ct = new CancellationTokenSource();
        FetchAssetBundles().Forget();
    }

    private void OnDestroy()
    {
        StopDownloads();
    }
    
    private void StopDownloads()
    {
        SaveCache();
        ct.Cancel();
        Debug.Log($"DOWNLOAD FINISHED FOR {totalFilesDownloaded}");
    }

    private async UniTaskVoid FetchAssetBundles()
    {
        try
        {
            string catalystSnapshotURL = await GetHashWithMostEntitiesAsync();
            
            string catalystContent = await GetRequestAsync(catalystSnapshotURL);

            if (!string.IsNullOrEmpty(catalystContent))
            {
                List<string> entityIds = ExtractEntityIds(catalystContent);
                List<UniTask> entityTasks = new List<UniTask>();
                int batchSize = 25;

                for (int i = 0; i < entityIds.Count; i++)
                {
                    entityTasks.Add(FetchEntityDataAsync(entityIds[i]));
                    // When we reach the batch size or the end of the list, wait for the tasks to complete
                    if (entityTasks.Count == batchSize || i == entityIds.Count - 1)
                    {
                        await UniTask.WhenAll(entityTasks);
                        entityTasks.Clear(); // Clear the list to start the next batch
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error: {ex.Message}");
        }
    }
    
    public async UniTask<string> GetHashWithMostEntitiesAsync()
    {
        // Assuming you're getting a list of hashes and their corresponding entity counts from a URL
        string jsonResponse = await GetRequestAsync(snapshotURL);
        
        List<SnapshotData> snapshots = JsonConvert.DeserializeObject<List<SnapshotData>>(jsonResponse);

        if (snapshots == null || snapshots.Count == 0)
        {
            Debug.LogError("No entity data available");
            return null;
        }
        

        
        foreach (var snapshotData in snapshots)
        {
            if (snapshotData.numberOfEntities > 19000 && snapshotData.numberOfEntities < 22000)
            {
                return $"{contentsURL}{snapshotData.hash}";
            }
        }
        
        var snapshotWithMostEntities = snapshots
            .OrderByDescending(snapshot => snapshot.numberOfEntities)
            .First();

        //Just in case the number of entities drastically change during the test, lets have a fallback
        return $"{contentsURL}{snapshotWithMostEntities.hash}";


    }


    private async UniTask FetchEntityDataAsync(string entityId)
    {
        if (ct.IsCancellationRequested)
            return;
        
        string entityUrl = contentsURL + entityId;

        string entityData = await GetRequestAsync(entityUrl);

        if (!string.IsNullOrEmpty(entityData))
        {
            var entity = JsonUtility.FromJson<DecentralandEntity>(entityData);

            if (entity.type == "wearable")
                await FetchManifestAndDownloadAssets(entityId);
        }
    }

    private async UniTask FetchManifestAndDownloadAssets(string entityId)
    {
        if (ct.IsCancellationRequested)
            return;
        
        string manifestUrl = string.Format(manifestUrlTemplate, entityId);
        string manifestData = await GetRequestAsync(manifestUrl);

        if (!string.IsNullOrEmpty(manifestData))
        {
            DecentralandManifest manifest = JsonUtility.FromJson<DecentralandManifest>(manifestData);

            if (manifest != null)
            {
                foreach (var file in manifest.files)
                {
                    if (!file.EndsWith("mac"))
                        continue;
                    string assetBundleUrl = string.Format(assetBundleUrlTemplate, manifest.version, file);
                    Hash128 hash = ComputeHash(manifest.version, file);

                    if (!assetBundleCache.ContainsKey(hash))
                    {
                        try
                        {
                            await DownloadAssetBundleAsync(hash, assetBundleUrl);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"Error while downloading AB: {ex.Message}");
                        }
                    }
                    else
                        Debug.Log($"AssetBundle already cached: {assetBundleCache[hash]}");
                }
            }
        }
    }

    public unsafe Hash128 ComputeHash(string version, string hash)
    {
        Span<char> hashBuilder = stackalloc char[version.Length + hash.Length];
        version.AsSpan().CopyTo(hashBuilder);
        hash.AsSpan().CopyTo(hashBuilder[version.Length..]);

        fixed (char* ptr = hashBuilder) { return Hash128.Compute(ptr, (uint)(sizeof(char) * hashBuilder.Length)); }
    }
    
    private async UniTask DownloadAssetBundleAsync(Hash128 hash, string assetBundleUrl)
    {
        if (ct.IsCancellationRequested)
            return;

        if (Caching.IsVersionCached(assetBundleUrl, hash))
            return;
        
        using (UnityWebRequest request = UnityWebRequestAssetBundle.GetAssetBundle(assetBundleUrl, hash))
        {
            await request.SendWebRequest().ToUniTask();
            if (request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.ProtocolError)
                Debug.LogError($"AssetBundle request error: {request.error}");
            else
            {
                Debug.Log($"Successfully downloaded AssetBundle from {assetBundleUrl} {totalFilesDownloaded}");
                assetBundleCache[hash] = assetBundleUrl; // Save the hash and URL in the cache after successful download
                totalFilesDownloaded++;
                if(totalFilesDownloaded >= totalFilesToDownload)
                    StopDownloads();
                //AssetBundle bundle = DownloadHandlerAssetBundle.GetContent(request);
            }
        }
    }



    private List<string> ExtractEntityIds(string text)
    {
        List<string> entityIds = new List<string>();

        Regex regex = new Regex("\"entityId\":\"(.*?)\"");
        MatchCollection matches = regex.Matches(text);

        foreach (Match match in matches)
        {
            string entityId = match.Groups[1].Value;
            entityIds.Add(entityId);
        }

        return entityIds;
    }

    private async UniTask<string> GetRequestAsync(string url)
    {
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            try
            {
                await request.SendWebRequest().ToUniTask();

                if (request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.ProtocolError)
                {
                    Debug.LogError($"Request error for URL {url}: {request.error}");
                    return null;
                }

                return request.downloadHandler.text;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Request failed: {ex.Message}");
                return null;
            }
        }
    }

    private void SaveCache()
    {
        try
        {
            string json = JsonConvert.SerializeObject(assetBundleCache, Formatting.Indented); // Use Newtonsoft.Json to serialize
            File.WriteAllText(Path.Combine(Application.dataPath, cacheFilePath), json);
            Debug.Log("Cache saved successfully.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to save cache: {ex.Message}");
        }
    }

    [Serializable]
    public class DecentralandEntity
    {
        public string type;
    }

    [Serializable]
    public class DecentralandManifest
    {
        public string version;
        public List<string> files;
    }
    
    [Serializable]
    public class SnapshotData
    {
        public string hash;
        public int numberOfEntities;
    }
}