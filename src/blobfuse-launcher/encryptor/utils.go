package main

import (
	"encoding/binary"
	"fmt"
	"os"
	"path/filepath"
	"sync"

	"github.com/Azure/azure-storage-fuse/v2/common/log"
	"github.com/Azure/azure-storage-fuse/v2/exported"
)

func formatListDirName(path string) string {
	// If we check the root directory, make sure we pass "" instead of "/".
	// If we aren't checking the root directory, then we want to extend the directory name so List returns all children and does not include the path itself.
	if path == "/" {
		path = ""
	} else if path != "" {
		path = exported.ExtendDirName(path)
	}
	return path
}

func checkForActualFileSize(fileHandle *os.File, currentFileSize int64, blockSize int64) (int64, error) {

	totalBlocks := currentFileSize / (blockSize + MetaSize)
	if currentFileSize < totalBlocks*(blockSize+MetaSize)+PaddingLengthSize {
		return 0, nil
	}

	paddingLength, err := readPaddingLength(fileHandle, currentFileSize)
	if err != nil {
		return 0, err
	}
	actualFileSize := currentFileSize - (paddingLength + (totalBlocks * MetaSize) + PaddingLengthSize)
	log.Info("Encryptor::checkForActualFileSize : current file size: %d , actualFileSize: %d", currentFileSize, actualFileSize)
	return actualFileSize, nil
}

func getFileHandleAndLastChunkMeta(
	handleMap *sync.Map,
	lastChunkMetaMap *sync.Map,
	fileName string,
	blockSize uint64,
	mountPoint string,
) (*os.File, *LastChunkMeta, error) {

	fileValue, found := handleMap.Load(fileName)
	if !found {
		// File not found in handleMap, open the file
		fileHandle, err := os.OpenFile(filepath.Join(mountPoint, fileName), os.O_RDWR, 0666)
		if err != nil {
			return nil, nil, fmt.Errorf("failed to open file %s: %w", fileName, err)
		}
		handleMap.Store(fileName, fileHandle)

		fileAttr, err := fileHandle.Stat()
		if err != nil {
			return nil, nil, fmt.Errorf("failed to get file attributes for %s: %w", fileName, err)
		}

		fileSize := fileAttr.Size()
		totalBlocks := fileSize / int64(blockSize+MetaSize)
		paddingBlockSize := fileSize % int64(blockSize+MetaSize)

		if paddingBlockSize == 0 {
			lastChunkMeta := &LastChunkMeta{farthestBlockSeen: -1, paddingLength: 0}
			lastChunkMetaMap.Store(fileName, lastChunkMeta)
			return fileHandle, lastChunkMeta, nil
		}

		if paddingBlockSize != PaddingLengthSize {
			return nil, nil, fmt.Errorf("unexpected padding block size for %s: %d", fileName, paddingBlockSize)
		}

		paddingLength, err := readPaddingLength(fileHandle, fileSize)
		if err != nil {
			return nil, nil, fmt.Errorf("failed to read padding length for %s: %w", fileName, err)
		}

		lastChunkMeta := &LastChunkMeta{
			farthestBlockSeen: totalBlocks,
			paddingLength:     paddingLength,
		}
		lastChunkMetaMap.Store(fileName, lastChunkMeta)
		return fileHandle, lastChunkMeta, nil
	}

	fileHandle, ok := fileValue.(*os.File)
	if !ok {
		return nil, nil, fmt.Errorf("unexpected type for file handle of %s", fileName)
	}

	metaValue, found := lastChunkMetaMap.Load(fileName)
	if !found {
		return nil, nil, fmt.Errorf("last chunk meta not found for %s", fileName)
	}

	lastChunkMeta, ok := metaValue.(*LastChunkMeta)
	if !ok {
		return nil, nil, fmt.Errorf("unexpected type for last chunk meta of %s", fileName)
	}

	return fileHandle, lastChunkMeta, nil
}

func writePaddingLength(fileHandle *os.File, lastChunkMeta *LastChunkMeta, blockSize uint64) error {
	paddingLengthBytes := make([]byte, PaddingLengthSize)
	binary.BigEndian.PutUint64(paddingLengthBytes, uint64(lastChunkMeta.paddingLength))
	endOffset := (lastChunkMeta.farthestBlockSeen + 1) * (int64(blockSize) + MetaSize)
	n, err := fileHandle.WriteAt(paddingLengthBytes, endOffset)
	if err != nil {
		log.Err("Encryptor: Error writing padding length to file: %s", err.Error())
		return err
	}
	log.Debug("Encryptor::CommitData : writing %d bytes, padding length %d at offset %d", n, lastChunkMeta.paddingLength, endOffset)
	return nil
}

func readPaddingLength(fileHandle *os.File, currentFileSize int64) (int64, error) {
	paddingLengthBytes := make([]byte, PaddingLengthSize)

	_, err := fileHandle.ReadAt(paddingLengthBytes, currentFileSize-PaddingLengthSize)
	if err != nil {
		log.Err("Encryptor: Error reading last %d bytes of file: %s", PaddingLengthSize, err)
		return 0, err
	}
	return int64(binary.BigEndian.Uint64(paddingLengthBytes)), nil
}
