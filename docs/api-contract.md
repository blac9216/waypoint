import pathlib
from typing import Optional
from sqlalchemy.ext.asyncio import AsyncSession

class ContentLibraryRepository:
    """
    Repository for Content Library entries, handling the compensating logic
    for filesystem-to-DB synchronization failures.
    """

    async def create(
        self, 
        content_id: str, 
        name: str, 
        directory: str 
    ) -> str:
        """
        IContentLibraryRepository.CreateAsync

        States:
        - Attempts to anchor a content library to the filesystem via directory creation.
        - If the filesystem directory creation fails, attempts to purge the DB row
          (Compensating Delete).
        - If that purge fails (e.g., connection drop), re-raises the original
          filesystem cause via chaining (AggregateException pattern).

        Residual Risk:
        The row may survive with no directory if the initial directory creation
        threw, hence the 'best effort' qualification in the contract.

        Conventions:
        - Base path logic follows `/api/v1` style `snake_case` fields.
        """
        # Initialize or Fetch the entity to track in DB
        library = self._session.get('content_libraries', content_id)

        try:
            # 1. Satisfy the "Directory Exists" constraint
            pathlib.Path(directory).mkdir(parents=True, exist_ok=True)
            
            # 2. Anchor the row in memory/DB
            self._session.add(library)
            
            # 3. Flush to ensure IDs align and state is committed
            await self._session.flush()

            return content_id
        except (OSError, pathlib.IsADirectoryError, pathlib.FileNotFoundError) as fs_ex:
            # 4. Compensating Delete: Remove the row if filesystem is unhappy
            try:
                self._session.delete(library)
            except Exception as cleanup_ex:
                # 5. The Residual: Re-throw the FS cause so user sees the 'why'
                # (e.g., "Directory expected but missing on first try")
                raise fs_ex from cleanup_ex