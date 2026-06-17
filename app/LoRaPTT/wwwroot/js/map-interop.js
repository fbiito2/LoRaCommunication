// 節點地圖 JS 互通（F-037）：MapLibre GL 初始化、自己/節點 markers、點選回呼、回到自己。
// 階段一底圖用 OpenFreeMap（免費、免 API key、可商用、OSM 向量）；階段二離線改本地 style 來源。
(function () {
    let map = null;                 // MapLibre 地圖實例
    let dotNetRef = null;           // .NET 物件參考（marker 點選回呼用）
    let selfMarker = null;          // 自己的 marker
    const nodeMarkers = {};         // id(hex) -> maplibregl.Marker

    // 底圖樣式：街道(OpenFreeMap 向量) / 衛星(NLSC 正射影像 PHOTO2 raster)
    const STYLES = {
        // 街道改用 NLSC 通用電子地圖(EMAP)，走本機協定 → 與衛星一致、可離線
        street: {
            version: 8,
            sources: {
                emap: {
                    type: 'raster',
                    tiles: ['loraoff://emap/{z}/{x}/{y}'],
                    tileSize: 256,
                    attribution: '© 內政部國土測繪中心'
                }
            },
            layers: [{ id: 'emap', type: 'raster', source: 'emap' }]
        },
        sat: {
            version: 8,
            sources: {
                nlsc: {
                    type: 'raster',
                    // 走本機協定：離線優先(讀本機已下載圖磚)，線上時自動補抓 NLSC 並快取
                    tiles: ['loraoff://sat/{z}/{x}/{y}'],
                    tileSize: 256,
                    attribution: '© 內政部國土測繪中心'
                }
            },
            layers: [{ id: 'nlsc', type: 'raster', source: 'nlsc' }]
        }
    };

    // CWA 雷達整合回波透明圖層(較大範圍)；範圍取自 CWA 開放資料 API（已驗證）。
    // image source 角點順序：左上,右上,右下,左下（[lon,lat]）；範圍 經 115.00-126.50 / 緯 17.75-29.25。
    // 圖檔由 .NET 端抓回（S3 無 CORS，WebGL 貼圖會被擋）→ 以 data URL 傳入。
    const RADAR_COORDS = [[115.00, 29.25], [126.50, 29.25], [126.50, 17.75], [115.00, 17.75]];
    let radarOn = false;
    let radarImg = null; // .NET 傳入的 data:image/png;base64,...

    // 加上雷達疊加層（半透明）
    function addRadar() {
        if (!map || !radarImg || map.getSource('cwaRadar')) return;
        map.addSource('cwaRadar', { type: 'image', url: radarImg, coordinates: RADAR_COORDS });
        map.addLayer({ id: 'cwaRadar', type: 'raster', source: 'cwaRadar', paint: { 'raster-opacity': 0.65 } });
    }
    // 移除雷達疊加層
    function removeRadar() {
        if (!map) return;
        if (map.getLayer('cwaRadar')) { map.removeLayer('cwaRadar'); }
        if (map.getSource('cwaRadar')) { map.removeSource('cwaRadar'); }
    }

    // ── 離線圖磚協定（F-037 階段二）────────────────────────────
    let protoRegistered = false;

    // base64 → Uint8Array
    function b64ToArr(b64) {
        const bin = atob(b64);
        const arr = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) { arr[i] = bin.charCodeAt(i); }
        return arr;
    }

    // 註冊 loraoff://sat/{z}/{x}/{y}：向 .NET 取圖磚（離線優先 + 線上自動補快取）。
    // 注意：MapLibre GL v4 的 addProtocol handler 須為 async、回傳 {data: ArrayBuffer}
    //（v4 已移除舊的 callback 介面，用 callback 會導致圖磚永遠載不出來）。
    function registerOfflineProtocol() {
        if (protoRegistered || typeof maplibregl === 'undefined') return;
        maplibregl.addProtocol('loraoff', async function (params) {
            const m = /loraoff:\/\/(sat|emap)\/(\d+)\/(\d+)\/(\d+)/.exec(params.url);
            if (!m || !dotNetRef) throw new Error('bad tile url');
            const b64 = await dotNetRef.invokeMethodAsync('GetOfflineTile',
                m[1], parseInt(m[2]), parseInt(m[3]), parseInt(m[4]));
            if (!b64) throw new Error('tile missing'); // 沒下載且離線 → 視為缺圖（空白）
            return { data: b64ToArr(b64).buffer };
        });
        protoRegistered = true;
    }

    window.loraMap = {
        // 初始化地圖。dotnet=DotNetObjectReference，lat/lon=初始中心，hasSelf=是否已有自身定位
        init: function (dotnet, lat, lon, hasSelf) {
            dotNetRef = dotnet;
            if (typeof maplibregl === 'undefined') {
                console.error('[loraMap] maplibre-gl.js 未載入');
                return;
            }
            registerOfflineProtocol(); // 註冊 loraoff:// 本機圖磚協定
            if (map) { try { map.remove(); } catch (e) { } map = null; }
            const center = hasSelf ? [lon, lat] : [121.5, 25.05]; // 無定位時預設台北
            try {
                map = new maplibregl.Map({
                    container: 'lora-map',
                    style: STYLES.street,
                    center: center,
                    zoom: 14
                });
            } catch (e) {
                console.error('[loraMap] 建立地圖失敗:', e && e.message);
                return;
            }
            map.on('load', function () { console.log('[loraMap] style 載入完成'); });
            map.on('error', function (e) {
                console.error('[loraMap] 地圖錯誤:', (e && e.error && e.error.message) || e);
            });
            map.addControl(new maplibregl.NavigationControl({ showCompass: false }), 'bottom-left');
            // 容器初始尺寸可能尚未定案 → 延遲 resize，避免空白/半截
            setTimeout(function () { if (map) { map.resize(); } }, 400);
            if (hasSelf) { this.setSelf(lat, lon); }
        },

        // 設定/更新自己的 marker（青色）
        setSelf: function (lat, lon) {
            if (!map) return;
            const lngLat = [lon, lat];
            if (!selfMarker) {
                const el = document.createElement('div');
                el.className = 'lora-self-dot';
                selfMarker = new maplibregl.Marker({ element: el }).setLngLat(lngLat).addTo(map);
            } else {
                selfMarker.setLngLat(lngLat);
            }
        },

        // 設定節點 markers。nodes=[{id,name,lat,lon}]（僅含有定位者）
        setNodes: function (nodes) {
            if (!map) return;
            const seen = {};
            nodes.forEach(function (n) {
                seen[n.id] = true;
                let m = nodeMarkers[n.id];
                if (!m) {
                    const el = document.createElement('div');
                    el.className = 'lora-node-dot';
                    if (n.color) { el.style.background = n.color; } // 每台一色（與清單共用）
                    el.title = n.id;
                    el.addEventListener('click', function (ev) {
                        ev.stopPropagation();
                        if (dotNetRef) { dotNetRef.invokeMethodAsync('OnNodeMarkerClick', n.id); }
                    });
                    m = new maplibregl.Marker({ element: el }).setLngLat([n.lon, n.lat]).addTo(map);
                    nodeMarkers[n.id] = m;
                } else {
                    m.setLngLat([n.lon, n.lat]);
                }
            });
            // 移除已不在清單中的 marker
            Object.keys(nodeMarkers).forEach(function (id) {
                if (!seen[id]) { nodeMarkers[id].remove(); delete nodeMarkers[id]; }
            });
        },

        // 平滑移動到自己
        flyToSelf: function (lat, lon) {
            if (map) { map.flyTo({ center: [lon, lat], zoom: 15 }); }
        },

        // 平滑移動到指定座標
        flyTo: function (lat, lon) {
            if (map) { map.flyTo({ center: [lon, lat], zoom: 16 }); }
        },

        // 切換底圖：'street' 街道 / 'sat' 衛星(NLSC)。Marker 為 DOM 浮層，setStyle 不影響。
        setBaseLayer: function (type) {
            if (!map) return;
            map.setStyle(STYLES[type] || STYLES.street);
            // setStyle 會重載 style、清掉雷達層 → 若雷達仍開著，待新 style 載好後重加
            if (radarOn) { map.once('styledata', addRadar); }
        },

        // 開/關 CWA 雷達疊加層；dataUrl = .NET 抓回的雷達 PNG（data:image/png;base64,...）
        toggleRadar: function (on, dataUrl) {
            radarOn = !!on;
            if (dataUrl) { radarImg = dataUrl; }
            if (radarOn) { addRadar(); } else { removeRadar(); }
        },

        // 取得目前地圖視野範圍與縮放（供「下載目前畫面」用）
        getView: function () {
            if (!map) return null;
            const b = map.getBounds();
            return {
                minLon: b.getWest(), minLat: b.getSouth(),
                maxLon: b.getEast(), maxLat: b.getNorth(),
                zoom: Math.floor(map.getZoom())
            };
        },

        // 釋放地圖（離開頁面）
        dispose: function () {
            if (map) { try { map.remove(); } catch (e) { } map = null; }
            selfMarker = null;
            Object.keys(nodeMarkers).forEach(function (id) { delete nodeMarkers[id]; });
            dotNetRef = null;
        }
    };
})();
