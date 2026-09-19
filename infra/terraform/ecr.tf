# Images are tagged with the git tree hash of app/, so a tag is a content
# address: the same source always produces the same tag, and CI skips the
# build when the tag already exists.

resource "aws_ecr_repository" "images" {
  for_each = toset(["server", "web"])

  name                 = "plaintextpantry/${each.key}"
  image_tag_mutability = "IMMUTABLE"
  force_delete         = true

  image_scanning_configuration {
    scan_on_push = false
  }
}

resource "aws_ecr_lifecycle_policy" "images" {
  for_each   = aws_ecr_repository.images
  repository = each.value.name

  policy = jsonencode({
    rules = [{
      rulePriority = 1
      description  = "keep the last 10 builds"
      selection = {
        tagStatus   = "any"
        countType   = "imageCountMoreThan"
        countNumber = 10
      }
      action = { type = "expire" }
    }]
  })
}
