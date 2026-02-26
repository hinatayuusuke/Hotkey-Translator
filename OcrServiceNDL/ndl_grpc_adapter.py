from __future__ import annotations

from ndl_core_engine import NdlOcrLiteEngine


class NdlGrpcAdapter:
    """
    Thin adapter for the NDLOCR engine used by gRPC server code.
    """

    def __init__(
        self,
        *,
        model_dir: str | None,
        config_dir: str | None,
        device: str,
        det_score_threshold: float,
        det_conf_threshold: float,
        det_iou_threshold: float,
    ) -> None:
        self._engine = NdlOcrLiteEngine(
            model_dir=model_dir,
            config_dir=config_dir,
            device=device,
            det_score_threshold=det_score_threshold,
            det_conf_threshold=det_conf_threshold,
            det_iou_threshold=det_iou_threshold,
        )

    def recognize(self, image_bytes: bytes) -> str:
        return self._engine.recognize(image_bytes)

    def close(self) -> None:
        # NOTE: NdlOcrLiteEngine has no explicit external resources to dispose.
        return None


# COMPAT: Temporary alias for in-flight imports; callers should migrate to NdlGrpcAdapter.
NdlGrpcOcrEngine = NdlGrpcAdapter
