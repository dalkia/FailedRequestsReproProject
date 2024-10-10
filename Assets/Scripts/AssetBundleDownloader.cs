using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

public class AssetBundleDownloader : MonoBehaviour
{

    public TextAsset assetBundleDictionary;
    private Dictionary<string, string> assetBundleCache;
    
    private const int concurrentDownloads = 20; // Z limit
    private const int downloadBatchPNG = 10; // X amount of PNG requests
    private const int downloadBatchAB = 1000; // Y amount of AB requests before PNG

    private SemaphoreSlim downloadSemaphore = new(concurrentDownloads);

    private CancellationTokenSource cts;

    private int totalFilesCacheDownloaded;
    
    void Start()
    {
        assetBundleCache = JsonConvert.DeserializeObject<Dictionary<string, string>>(assetBundleDictionary.text);
        Debug.Log($"WE HAVE {assetBundleCache.Count} VALUES");
        cts = new CancellationTokenSource();
        StartDownloadsBatchedAmount().Forget();
    }

    private void OnDestroy()
    {
        cts.Cancel();
    }

    async UniTask StartDownloadsBatchedAmount()
    {
        List<UniTask> abRequests = new List<UniTask>();
        int abCounter = 0;

        int stopCounter = 0;
        foreach (var keyValuePair in assetBundleCache)
        {
            abRequests.Add(DownloadAssetBundle(Hash128.Parse(keyValuePair.Key), keyValuePair.Value));
            abCounter++;
            if (abCounter >= downloadBatchAB)
            {
                await UniTask.WhenAll(abRequests);
                abRequests.Clear();
                await ProcessPngRequests(downloadBatchPNG);
                abCounter = 0;
            }

            stopCounter++;
            if (stopCounter > 1500)
                break;
        }

        /*
        foreach (var (hash, url) in assetBundleCache)
        {
            abRequests.Add(DownloadAssetBundle(Hash128.Parse(hash), url));
            abCounter++;
            if (abCounter >= downloadBatchAB)
            {
                await UniTask.WhenAll(abRequests);
                abRequests.Clear();
                await ProcessPngRequests(downloadBatchPNG);
                abCounter = 0;
            }
        }
        */
        
        if (abRequests.Count > 0)
        {
            await UniTask.WhenAll(abRequests);
            await ProcessPngRequests(downloadBatchPNG);
        }
    }
    
    async UniTask StartDownloadsOneByOne()
    {
        List<UniTask> abRequests = new List<UniTask>();
        int abCounter = 0;

        foreach (var (hash, url) in assetBundleCache)
        {
            abRequests.Add(DownloadAssetBundle(Hash128.Parse(hash), url));
            abRequests.Add(DoGetRequest());
        }
        
        await UniTask.WhenAll(abRequests);
    }
    
    async UniTask ProcessPngRequests(int pngRequestCount)
    {
        List<UniTask> pngRequests = new List<UniTask>();

        // Add X PNG requests
        for (int i = 0; i < pngRequestCount; i++)
        {
            pngRequests.Add(DoGetRequest());
        }

        // Wait for all PNG requests to complete before continuing
        await UniTask.WhenAll(pngRequests);
    }

    async UniTask DoGetRequest()
    {
        if (cts.Token.IsCancellationRequested)
            return;
        await downloadSemaphore.WaitAsync(cts.Token);
        Debug.Log("START: PNG");
        using UnityWebRequest request = UnityWebRequest.Get("https://res.soulmagic.online/v093/ui_atlas_5.png");
        try
        {
            
            await request.SendWebRequest().ToUniTask();
            if (request.result == UnityWebRequest.Result.Success)
                Debug.Log("SUCCESS: PNG");
        }
        catch (Exception e)
        {
            Debug.Log($"FAIL: PNG {e.Message} AT COUNT {totalFilesCacheDownloaded}");
        }
        finally
        {
            downloadSemaphore.Release();
        }
    }
    
    async UniTask DownloadAssetBundle(Hash128 hash, string assetBundleUrl)
    {
        if (cts.Token.IsCancellationRequested)
            return;
        await downloadSemaphore.WaitAsync(cts.Token);
        
        if (Caching.IsVersionCached(assetBundleUrl, hash))
            Debug.Log("CACHED VERSION HIT");
        else
            Debug.Log("CACHED VERSION MISS");
        
        using (UnityWebRequest request = UnityWebRequestAssetBundle.GetAssetBundle(assetBundleUrl, hash))
        {
            Debug.Log("START: AB");

            await request.SendWebRequest().ToUniTask();
            if (request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.ProtocolError)
                Debug.LogError($"FAIL: AB {request.error}");
            else
            {
                AssetBundle bundle = DownloadHandlerAssetBundle.GetContent(request);
                if (bundle != null)
                {
                    totalFilesCacheDownloaded++;
                    Debug.Log($"SUCCESS: AB {assetBundleUrl} TOTAL DOWNLOADED {totalFilesCacheDownloaded}");
                }
            }
            downloadSemaphore.Release();
        }
    }

  
}
