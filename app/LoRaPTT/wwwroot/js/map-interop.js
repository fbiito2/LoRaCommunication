// 節點地圖 JS 互通（F-037）：MapLibre GL 初始化、自己/節點 markers、點選回呼、回到自己。
// 階段一底圖用 OpenFreeMap（免費、免 API key、可商用、OSM 向量）；階段二離線改本地 style 來源。
(function () {
    let map = null;                 // MapLibre 地圖實例
    let dotNetRef = null;           // .NET 物件參考（marker 點選回呼用）
    let selfMarker = null;          // 自己的 marker
    const nodeMarkers = {};         // id(hex) -> maplibregl.Marker

    // 底圖樣式：街道(OpenFreeMap 向量) / 衛星(NLSC 正射影像 PHOTO2 raster)
    const STYLES = {
        street: 'https://tiles.openfreemap.org/styles/liberty',
        sat: {
            version: 8,
            sources: {
                nlsc: {
                    type: 'raster',
                    tiles: ['https://wmts.nlsc.gov.tw/wmts/PHOTO2/default/GoogleMapsCompatible/{z}/{y}/{x}'],
                    tileSize: 256,
                    attribution: '© 內政部國土測繪中心'
                }
            },
            layers: [{ id: 'nlsc', type: 'raster', source: 'nlsc' }]
        }
    };

    window.loraMap = {
        // 初始化地圖。dotnet=DotNetObjectReference，lat/lon=初始中心，hasSelf=是否已有自身定位
        init: function (dotnet, lat, lon, hasSelf) {
            dotNetRef = dotnet;
            if (typeof maplibregl === 'undefined') {
                console.error('[loraMap] maplibre-gl.js 未載入');
                return;
            }
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
            if (map) { map.setStyle(STYLES[type] || STYLES.street); }
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
