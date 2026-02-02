[0] text='I'm getting the error "Insert disk 3 in drive c:"..' conf=0.967 box=[14.0, 155.0, 509.0, 17.0]
[1] text='rsions 1.0 to 1.2 are picky about where they are installed; the usual versior.' conf=0.984 box=[0.0, 194.0, 799.0, 24.0]
[2] text='abandonware sites is 1.0. These cannot be installed in a directory more.' conf=0.978 box=[5.0, 214.0, 755.0, 26.0]
[3] text='n one level deep from C:\ (so c:\GAMEs\pooL\ will never work). PooL.cFG' conf=0.924 box=[1.0, 240.0, 757.0, 17.0]
[4] text='t also be edited (with any text editor) to point to the correct directory..' conf=0.986 box=[1.0, 255.0, 773.0, 26.0]
[5] text='must also include a backslash at the end of the directory name..' conf=0.993 box=[8.0, 276.0, 661.0, 26.0]

この現象は、**ディープラーニング（CNN）ベースの物体検出モデルによくある「画像の端にある特徴量をうまく拾えない」という特性**に起因しています。

### なぜ左側が見切れるのか？

ログの `box` 座標を見ると理由がわかります。

> `box=[0.0, 194.0, 799.0, 24.0]`

`x=0.0` となっています。これは、**文字検出ボックスが画像の左端ギリギリ（または少し外）**に発生してしまっていることを意味します。PaddleOCR（DBNetなど）の検出モデルは、畳み込み処理を行う際に画像の端（パディングがない状態）にある文字の「開始部分」を背景やノイズと区別できず、ボックスを少し内側に作ってしまうことがあります。その結果、最初の1文字（`V` や `4`）がボックス外にはみ出し、認識フェーズで無視されています。

---

### 解決策

コードのロジックではなく、**前処理（Preprocessing）またはパラメータ設定**で解決できます。

#### 解決策1：画像に余白（Padding）を追加する【推奨】

推論させる前に、画像の周囲に少し余白（黒または白）を追加すると、モデルが「ここが文字の端だ」と認識しやすくなり、劇的に改善します。

`recognize` メソッド内の `Image.open` の直後に以下の処理を追加してみてください。

```python
from PIL import Image, ImageOps

# ... (中略)

    def recognize(self, image_bytes: bytes) -> str:
        # ... (GPUチェックなど) ...

        image = Image.open(io.BytesIO(image_bytes)).convert("RGB")

        # --- 【追加】余白を追加する処理 ---
        # 画像の周りに 50px 程度の余白を追加（色は画像の平均色や黒/白など）
        # ここでは黒背景のようなので黒(0,0,0)で埋めますが、
        # 汎用的にするなら ImageOps.expand を使うのが手軽です。
        padding = 50
        image = ImageOps.expand(image, border=padding, fill=(0, 0, 0)) 
        # -------------------------------

        img_np = np.array(image)
        
        # 推論
        result = self._engine.predict(img_np)
        
        lines = self._parse_v5_predict_result(result)
        
        # --- 【追加】座標を元に戻す ---
        # 余白を足した分、検出された box の座標 (x, y) を -50 する必要があります
        for line in lines:
            box = line["box"] # [x, y, w, h]
            line["box"] = [box[0] - padding, box[1] - padding, box[2], box[3]]
        # ---------------------------

        return json.dumps({"lines": lines}, ensure_ascii=False)

```

#### 解決策2：検出パラメータ `unclip_ratio` を上げる

検出されたテキストボックスを「どれくらい膨らませるか」というパラメータを調整します。デフォルトより大きくすることで、見切れている文字を含めることができる場合があります。

`__init__` の `kwargs` に以下を追加してみてください。

```python
        kwargs: dict[str, Any] = {
            "lang": lang_for_engine,
            "ocr_version": ocr_version,
            "det_db_unclip_ratio": 2.0,  # デフォルトは1.5～1.6程度。これを大きくするとボックスが広がる
            # ... 他の設定
        }

```


