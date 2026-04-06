from typing import List, Optional

from pydantic import BaseModel, Field

from .cleanroom import Identity


class CollaborationContext(BaseModel):
    name: str
    collaborator_id: str
    governance_client_name: str
    identities: List[Identity]


class CollaborationSpecification(BaseModel):
    collaborations: Optional[List[CollaborationContext]] = Field(default_factory=list)
    current_context: Optional[str] = None

    def check_collaboration_context(
        self, collaboration_name: str
    ) -> tuple[bool, Optional[int], Optional[CollaborationContext]]:
        self.collaborations = self.collaborations or []
        for index, x in enumerate(self.collaborations):
            if x.name == collaboration_name:
                return True, index, x

        return False, None, None

    def get_collaboration_context(
        self, collaboration_name: str
    ) -> CollaborationContext:
        from ..exceptions.exception import CleanroomSpecificationError, ErrorCode

        exists, _, collaboration_context = self.check_collaboration_context(
            collaboration_name
        )
        if not exists:
            raise CleanroomSpecificationError(
                ErrorCode.CollaborationNotFound,
                (f"Collaboration {collaboration_name} not found."),
            )

        assert (
            collaboration_context is not None
        ), "Collaboration entry should not be None at this point."
        return collaboration_context

    def add_collaboration_context(
        self,
        collaboration_context: CollaborationContext,
        set_current_context: bool = True,
    ) -> None:
        from ..exceptions.exception import CleanroomSpecificationError, ErrorCode

        exists, _, _ = self.check_collaboration_context(collaboration_context.name)
        if exists:
            raise CleanroomSpecificationError(
                ErrorCode.CollaborationAlreadyExists,
                (f"Collaboration {collaboration_context.name} already exists."),
            )

        self.collaborations = self.collaborations or []
        self.collaborations.append(collaboration_context)
        if set_current_context:
            self.current_context = collaboration_context.name

    def get_active_collaboration_context(self) -> CollaborationContext:
        from ..exceptions.exception import CleanroomSpecificationError, ErrorCode

        if self.current_context is None:
            raise CleanroomSpecificationError(
                ErrorCode.CurrentCollaborationNotSet,
                (f"Current collaboration context is not set."),
            )
        return self.get_collaboration_context(self.current_context)

    def set_active_collaboration_context(self, collaboration_name: str) -> None:
        collaboration_context = self.get_collaboration_context(collaboration_name)
        self.current_context = collaboration_context.name

    def clear_active_collaboration_context(self) -> None:
        self.current_context = None
