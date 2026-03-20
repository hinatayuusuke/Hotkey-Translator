from __future__ import annotations

import ctypes
import os
from contextlib import ExitStack
from ctypes import POINTER, Structure, byref, c_char_p, c_float, c_int32, c_int64, c_ubyte
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Iterable

from PIL import Image, ImageOps


MODEL_NAME = "oneocr.onemodel"
DLL_NAME = "oneocr.dll"
ORT_NAME = "onnxruntime.dll"
MODEL_KEY = b"kj)TGtrK>f]b[Piow.gU+nC@s\"\"\"\"\"\"4"
MIN_IMAGE_DIMENSION = 50
MAX_IMAGE_DIMENSION = 10_000
DEFAULT_MAX_LINE_COUNT = 1_000


class OneOcrError(RuntimeError):
    pass


class ImageStructure(Structure):
    _fields_ = [
        ("type", c_int32),
        ("width", c_int32),
        ("height", c_int32),
        ("_reserved", c_int32),
        ("step_size", c_int64),
        ("data_ptr", POINTER(c_ubyte)),
    ]


class BoundingPolygon(Structure):
    _fields_ = [
        ("x1", c_float),
        ("y1", c_float),
        ("x2", c_float),
        ("y2", c_float),
        ("x3", c_float),
        ("y3", c_float),
        ("x4", c_float),
        ("y4", c_float),
    ]


BoundingPolygonPointer = POINTER(BoundingPolygon)


DLL_FUNCTIONS = [
    ("CreateOcrInitOptions", [POINTER(c_int64)], c_int64),
    ("OcrInitOptionsSetUseModelDelayLoad", [c_int64, c_ubyte], c_int64),
    ("CreateOcrPipeline", [c_char_p, c_char_p, c_int64, POINTER(c_int64)], c_int64),
    ("CreateOcrProcessOptions", [POINTER(c_int64)], c_int64),
    ("OcrProcessOptionsSetMaxRecognitionLineCount", [c_int64, c_int64], c_int64),
    ("RunOcrPipeline", [c_int64, POINTER(ImageStructure), c_int64, POINTER(c_int64)], c_int64),
    ("GetImageAngle", [c_int64, POINTER(c_float)], c_int64),
    ("GetOcrLineCount", [c_int64, POINTER(c_int64)], c_int64),
    ("GetOcrLine", [c_int64, c_int64, POINTER(c_int64)], c_int64),
    ("GetOcrLineContent", [c_int64, POINTER(c_char_p)], c_int64),
    ("GetOcrLineBoundingBox", [c_int64, POINTER(BoundingPolygonPointer)], c_int64),
    ("GetOcrLineWordCount", [c_int64, POINTER(c_int64)], c_int64),
    ("GetOcrWord", [c_int64, c_int64, POINTER(c_int64)], c_int64),
    ("GetOcrWordContent", [c_int64, POINTER(c_char_p)], c_int64),
    ("GetOcrWordBoundingBox", [c_int64, POINTER(BoundingPolygonPointer)], c_int64),
    ("GetOcrWordConfidence", [c_int64, POINTER(c_float)], c_int64),
    ("ReleaseOcrResult", [c_int64], None),
    ("ReleaseOcrInitOptions", [c_int64], None),
    ("ReleaseOcrPipeline", [c_int64], None),
    ("ReleaseOcrProcessOptions", [c_int64], None),
]


@dataclass(slots=True)
class OcrWord:
    text: str
    confidence: float | None
    bbox: list[float]
    polygon: list[list[float]]


@dataclass(slots=True)
class OcrLine:
    text: str
    bbox: list[float]
    polygon: list[list[float]]
    words: list[OcrWord]


@dataclass(slots=True)
class OcrResult:
    full_text: str
    image_angle: float | None
    image_width: int
    image_height: int
    lines: list[OcrLine]

    def to_dict(self) -> dict:
        return {
            "fullText": self.full_text,
            "imageAngle": self.image_angle,
            "imageWidth": self.image_width,
            "imageHeight": self.image_height,
            "lines": [asdict(line) for line in self.lines],
            "words": [asdict(word) for line in self.lines for word in line.words],
        }


class OneOcrBridge:
    def __init__(self, vendor_dir: str | Path, max_line_count: int = DEFAULT_MAX_LINE_COUNT) -> None:
        self.vendor_dir = Path(vendor_dir).resolve()
        self.max_line_count = max_line_count
        self._dll_directory_stack = ExitStack()
        self._ocr_dll = None
        self._init_options = c_int64()
        self._pipeline = c_int64()
        self._process_options = c_int64()
        try:
            self._load_runtime()
            self._create_pipeline_objects()
        except Exception:
            self.close()
            raise

    def close(self) -> None:
        if self._ocr_dll is None:
            return
        if self._process_options.value:
            self._ocr_dll.ReleaseOcrProcessOptions(self._process_options.value)
        if self._pipeline.value:
            self._ocr_dll.ReleaseOcrPipeline(self._pipeline.value)
        if self._init_options.value:
            self._ocr_dll.ReleaseOcrInitOptions(self._init_options.value)
        self._dll_directory_stack.close()
        self._ocr_dll = None

    def __enter__(self) -> "OneOcrBridge":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.close()

    def recognize_image(self, image_path: str | Path) -> OcrResult:
        image = Image.open(image_path)
        try:
            return self.recognize_pil(image)
        finally:
            image.close()

    def recognize_pil(self, image: Image.Image) -> OcrResult:
        image = ImageOps.exif_transpose(image)
        if image.mode != "RGBA":
            image = image.convert("RGBA")

        width, height = image.size
        if not self._is_supported_image_size(width, height):
            raise OneOcrError(
                f"Unsupported image size {width}x{height}. "
                f"Expected each dimension between {MIN_IMAGE_DIMENSION} and {MAX_IMAGE_DIMENSION}."
            )

        # WHY: b1tg/win11-oneocr passes BGRA pixels to oneocr.dll. We keep the same byte order
        # so the Python path matches the proven native call shape instead of guessing.
        bgra_bytes = image.tobytes("raw", "BGRA")
        buffer = (c_ubyte * len(bgra_bytes)).from_buffer_copy(bgra_bytes)
        image_struct = ImageStructure(
            type=3,
            width=width,
            height=height,
            _reserved=0,
            step_size=width * 4,
            data_ptr=ctypes.cast(buffer, POINTER(c_ubyte)),
        )

        result_handle = c_int64()
        self._check_result(
            self._ocr_dll.RunOcrPipeline(
                self._pipeline,
                byref(image_struct),
                self._process_options,
                byref(result_handle),
            ),
            "RunOcrPipeline failed",
        )

        try:
            return self._parse_result(result_handle.value, width=width, height=height)
        finally:
            self._ocr_dll.ReleaseOcrResult(result_handle.value)

    def _load_runtime(self) -> None:
        missing = [name for name in (DLL_NAME, MODEL_NAME, ORT_NAME) if not (self.vendor_dir / name).exists()]
        if missing:
            raise OneOcrError(
                "Missing vendor files: "
                + ", ".join(missing)
                + f". Place them in {self.vendor_dir}."
            )

        if hasattr(os, "add_dll_directory"):
            self._dll_directory_stack.enter_context(os.add_dll_directory(str(self.vendor_dir)))

        dll_path = self.vendor_dir / DLL_NAME
        self._ocr_dll = ctypes.WinDLL(str(dll_path))
        for name, argtypes, restype in DLL_FUNCTIONS:
            try:
                function = getattr(self._ocr_dll, name)
            except AttributeError as exc:
                raise OneOcrError(f"Missing DLL export: {name}") from exc
            function.argtypes = argtypes
            function.restype = restype

    def _create_pipeline_objects(self) -> None:
        self._check_result(
            self._ocr_dll.CreateOcrInitOptions(byref(self._init_options)),
            "CreateOcrInitOptions failed",
        )
        self._check_result(
            self._ocr_dll.OcrInitOptionsSetUseModelDelayLoad(self._init_options, 0),
            "OcrInitOptionsSetUseModelDelayLoad failed",
        )

        model_path = ctypes.create_string_buffer(str(self.vendor_dir / MODEL_NAME).encode("utf-8"))
        model_key = ctypes.create_string_buffer(MODEL_KEY)
        self._check_result(
            self._ocr_dll.CreateOcrPipeline(
                model_path,
                model_key,
                self._init_options,
                byref(self._pipeline),
            ),
            "CreateOcrPipeline failed",
        )
        self._check_result(
            self._ocr_dll.CreateOcrProcessOptions(byref(self._process_options)),
            "CreateOcrProcessOptions failed",
        )
        self._check_result(
            self._ocr_dll.OcrProcessOptionsSetMaxRecognitionLineCount(self._process_options, self.max_line_count),
            "OcrProcessOptionsSetMaxRecognitionLineCount failed",
        )

    def _parse_result(self, result_handle: int, width: int, height: int) -> OcrResult:
        line_count = c_int64()
        self._check_result(
            self._ocr_dll.GetOcrLineCount(result_handle, byref(line_count)),
            "GetOcrLineCount failed",
        )

        lines = [self._read_line(result_handle, index) for index in range(line_count.value)]
        image_angle = c_float()
        image_angle_value: float | None = None
        if self._ocr_dll.GetImageAngle(result_handle, byref(image_angle)) == 0:
            image_angle_value = image_angle.value

        return OcrResult(
            full_text="\n".join(line.text for line in lines if line.text),
            image_angle=image_angle_value,
            image_width=width,
            image_height=height,
            lines=lines,
        )

    def _read_line(self, result_handle: int, line_index: int) -> OcrLine:
        line_handle = c_int64()
        self._check_result(
            self._ocr_dll.GetOcrLine(result_handle, line_index, byref(line_handle)),
            f"GetOcrLine failed for index {line_index}",
        )
        text = self._read_text(line_handle.value, self._ocr_dll.GetOcrLineContent)
        polygon = self._read_polygon(line_handle.value, self._ocr_dll.GetOcrLineBoundingBox)
        word_count = c_int64()
        self._check_result(
            self._ocr_dll.GetOcrLineWordCount(line_handle.value, byref(word_count)),
            f"GetOcrLineWordCount failed for index {line_index}",
        )
        words = [self._read_word(line_handle.value, word_index) for word_index in range(word_count.value)]
        return OcrLine(
            text=text,
            bbox=self._polygon_to_bbox(polygon),
            polygon=polygon,
            words=words,
        )

    def _read_word(self, line_handle: int, word_index: int) -> OcrWord:
        word_handle = c_int64()
        self._check_result(
            self._ocr_dll.GetOcrWord(line_handle, word_index, byref(word_handle)),
            f"GetOcrWord failed for index {word_index}",
        )
        text = self._read_text(word_handle.value, self._ocr_dll.GetOcrWordContent)
        polygon = self._read_polygon(word_handle.value, self._ocr_dll.GetOcrWordBoundingBox)
        confidence = c_float()
        confidence_value: float | None = None
        if self._ocr_dll.GetOcrWordConfidence(word_handle.value, byref(confidence)) == 0:
            confidence_value = confidence.value
        return OcrWord(
            text=text,
            confidence=confidence_value,
            bbox=self._polygon_to_bbox(polygon),
            polygon=polygon,
        )

    def _read_text(self, handle: int, accessor) -> str:
        content = c_char_p()
        self._check_result(accessor(handle, byref(content)), f"{accessor.__name__} failed")
        return content.value.decode("utf-8", errors="replace") if content.value else ""

    def _read_polygon(self, handle: int, accessor) -> list[list[float]]:
        polygon_ptr = BoundingPolygonPointer()
        self._check_result(accessor(handle, byref(polygon_ptr)), f"{accessor.__name__} failed")
        if not polygon_ptr:
            raise OneOcrError(f"{accessor.__name__} returned no bounding polygon")
        polygon = polygon_ptr.contents
        return [
            [polygon.x1, polygon.y1],
            [polygon.x2, polygon.y2],
            [polygon.x3, polygon.y3],
            [polygon.x4, polygon.y4],
        ]

    def _polygon_to_bbox(self, polygon: Iterable[Iterable[float]]) -> list[float]:
        xs = [point[0] for point in polygon]
        ys = [point[1] for point in polygon]
        min_x = min(xs)
        min_y = min(ys)
        max_x = max(xs)
        max_y = max(ys)
        return [min_x, min_y, max_x - min_x, max_y - min_y]

    def _is_supported_image_size(self, width: int, height: int) -> bool:
        return (
            MIN_IMAGE_DIMENSION <= width <= MAX_IMAGE_DIMENSION
            and MIN_IMAGE_DIMENSION <= height <= MAX_IMAGE_DIMENSION
        )

    def _check_result(self, result_code: int, message: str) -> None:
        if result_code != 0:
            raise OneOcrError(f"{message} (code={result_code})")
