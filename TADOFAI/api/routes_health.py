

from __future__ import annotations

from fastapi import APIRouter, Request

router = APIRouter(tags=["health"])


@router.get("/api/health")
async def get_health(request: Request) -> dict:
    services = request.app.state.services
    return await services.health_snapshot()
