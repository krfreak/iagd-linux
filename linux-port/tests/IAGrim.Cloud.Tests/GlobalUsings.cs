// CloudIdentity and CloudTombstone live in IAGrim.Platform: a cloud id is assigned when an item
// is created, which is a write path that knows nothing about online sync. Imported globally so
// the tests read the same way the production code does.
global using IAGrim.Platform;
