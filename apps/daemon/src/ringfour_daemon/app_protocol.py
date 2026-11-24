"""Strict readers for the separately negotiated App protocol.

AP-03 wires negotiation/inventory into ConnectionManager. App command dispatch
stays disabled until the installation and executor milestones.
"""

from __future__ import annotations

import json
import re
from datetime import datetime
from decimal import Decimal, DecimalException
from typing import Annotated, Literal, TypeVar

from pydantic import (
    AfterValidator,
    BaseModel,
    ConfigDict,
    Field,
    StringConstraints,
    field_validator,
    model_validator,
)

MAX_BODY_BYTES = 65536
Id = Annotated[
    str,
    StringConstraints(
        pattern=r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"
    ),
]


def _timestamp(value: str) -> str:
    datetime.fromisoformat(value)
    return value


Timestamp = Annotated[
    str,
    StringConstraints(pattern=r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?Z$"),
    AfterValidator(_timestamp),
]
Capability = Annotated[str, StringConstraints(pattern=r"^[a-z][a-z0-9_]{0,63}$")]
Name = Annotated[
    str, StringConstraints(min_length=1, max_length=100, pattern=r"^[^\x00-\x1f\x7f]+$")
]
Text = Annotated[str, StringConstraints(min_length=1, max_length=120)]
Version = Annotated[int, Field(ge=1, le=1)]
Sequence = Annotated[int, Field(ge=1, le=9007199254740991)]


class ProtocolError(ValueError):
    pass


class ProtocolModel(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)


class NegotiationRequest(ProtocolModel):
    supports_app_installations: bool = False
    supports_app_runs: bool = False
    session_id: Id
    deployment_namespace: Id
    supported_app_protocol_versions: list[Annotated[int, Field(ge=1, le=65535)]] = (
        Field(min_length=1, max_length=8)
    )

    @field_validator("supported_app_protocol_versions")
    @classmethod
    def unique_versions(cls, values):
        if len(values) != len(set(values)):
            raise ValueError("duplicate App protocol version")
        return values


class NegotiationResponse(ProtocolModel):
    session_id: Id
    deployment_namespace: Id
    selected_app_protocol_version: Version
    server_time: Timestamp


class Profile(ProtocolModel):
    name: Text
    version: Text
    model: Text


class Source(ProtocolModel):
    kind: Literal["agent_welcome"]
    protocol: Version
    profile: Profile


class AgentObservation(ProtocolModel):
    observed_at: Timestamp
    source: Source
    capabilities: list[Capability] = Field(max_length=64)
    instances: dict[Capability, Annotated[list[Name], Field(max_length=64)]] = Field(
        max_length=16
    )

    @model_validator(mode="after")
    def validate_inventory(self):
        if len(self.capabilities) != len(set(self.capabilities)):
            raise ValueError("duplicate capability")
        for values in self.instances.values():
            if len(values) != len(set(values)):
                raise ValueError("duplicate instance")
        arms = self.instances.get("arms", [])
        if arms and (
            "groups" not in self.capabilities
            or not set(arms) <= set(self.instances.get("groups", []))
        ):
            raise ValueError("arms must be named groups with the groups capability")
        return self


class AvailableInventory(ProtocolModel):
    state: Literal["available"]
    attempted_at: Timestamp
    current: AgentObservation


class UnavailableInventory(ProtocolModel):
    state: Literal["unavailable"]
    attempted_at: Timestamp
    reason: Literal[
        "agent_unreachable", "handshake_invalid", "unsupported_agent_protocol"
    ]
    last_successful: AgentObservation | None


Inventory = Annotated[
    AvailableInventory | UnavailableInventory, Field(discriminator="state")
]
Fact = Annotated[
    str,
    StringConstraints(
        min_length=1, max_length=120, pattern=r"^[a-z0-9][a-z0-9._:+-]*$"
    ),
]
PlatformName = Annotated[str, StringConstraints(pattern=r"^[a-z][a-z0-9_]{0,31}$")]


class Platform(ProtocolModel):
    os: PlatformName
    architecture: PlatformName
    native_facts: list[Fact] = Field(max_length=16)

    @field_validator("native_facts")
    @classmethod
    def unique_facts(cls, values):
        if len(values) != len(set(values)):
            raise ValueError("duplicate native fact")
        return values


class InventoryReport(ProtocolModel):
    app_protocol_version: Version
    deployment_namespace: Id
    report_id: Id
    report_sequence: Sequence
    platform: Platform
    inventory: Inventory


class RobotAppStatus(ProtocolModel):
    robot_id: Id
    connectivity: Literal["online", "offline"]
    last_seen_at: Timestamp | None
    support: Literal["not_negotiated", "supported", "upgrade_required"]
    negotiated_at: Timestamp | None
    deployment_namespace: Id | None
    report_sequence: Annotated[int, Field(ge=0, le=9007199254740991)]
    received_at: Timestamp | None
    platform: Platform | None
    inventory: Inventory | None

    @model_validator(mode="after")
    def validate_projection(self):
        if self.support == "supported" and (
            self.negotiated_at is None or self.deployment_namespace is None
        ):
            raise ValueError("supported projection requires negotiation")
        fields = (self.received_at, self.platform, self.inventory)
        if self.report_sequence == 0 and any(item is not None for item in fields):
            raise ValueError("no report cannot imply an observation")
        if self.report_sequence > 0 and (
            any(item is None for item in fields) or self.deployment_namespace is None
        ):
            raise ValueError("reported projection requires provenance")
        return self


T = TypeVar("T", bound=ProtocolModel)


def _unique_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ProtocolError("duplicate JSON member")
        value[key] = item
    return value


def _validate_scalars(value):
    if isinstance(value, str):
        if any(0xD800 <= ord(char) <= 0xDFFF for char in value):
            raise ProtocolError("invalid Unicode")
    elif isinstance(value, dict):
        for key, item in value.items():
            _validate_scalars(key)
            _validate_scalars(item)
    elif isinstance(value, list):
        for item in value:
            _validate_scalars(item)


def _non_json_number(_):
    raise ProtocolError("non-JSON number")


def _integer_number(token):
    # JSON Schema integer includes integral forms such as 1.0 and 1e0.
    # Preserve exactness; do not coerce strings, booleans or fractional values.
    try:
        number = Decimal(token)
        if abs(number) <= 9007199254740991 and number == number.to_integral_value():
            return int(number)
    except DecimalException:
        pass
    raise ProtocolError("nonintegral or unsafe number")


def parse(model: type[T], raw: str) -> T:
    """Validate raw input before typed decoding; never coerce or fetch schemas."""
    try:
        if len(raw.encode("utf-8")) > MAX_BODY_BYTES:
            raise ProtocolError("App protocol message exceeds byte limit")
        value = json.loads(
            raw,
            object_pairs_hook=_unique_object,
            parse_float=_integer_number,
            parse_constant=_non_json_number,
        )
        _validate_scalars(value)
        return model.model_validate(value)
    except (ValueError, TypeError, RecursionError) as error:
        raise ProtocolError("invalid App protocol input") from error


def select_version(offer: NegotiationRequest) -> int:
    if 1 not in offer.supported_app_protocol_versions:
        raise ProtocolError("daemon_upgrade_required")
    return 1


def validate_selection(
    offer: NegotiationRequest, response: NegotiationResponse
) -> None:
    if (
        response.session_id != offer.session_id
        or response.deployment_namespace != offer.deployment_namespace
        or response.selected_app_protocol_version
        not in offer.supported_app_protocol_versions
    ):
        raise ProtocolError("invalid App negotiation selection")


def ownership_labels(
    robot_id: str, deployment_namespace: str, installation_id: str, run_id: str
) -> dict[str, str]:
    """Construct canonical labels only from validated durable ownership IDs."""
    values = [robot_id, deployment_namespace, installation_id, run_id]
    if not all(
        re.fullmatch(
            r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", value
        )
        for value in values
    ):
        raise ProtocolError("invalid ownership ID")
    return dict(
        zip(
            (
                "eu.ringfour.robot-id",
                "eu.ringfour.deployment-namespace",
                "eu.ringfour.installation-id",
                "eu.ringfour.run-id",
            ),
            values,
            strict=True,
        )
    )
